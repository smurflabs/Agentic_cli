Option Strict On
Option Explicit On
Option Infer On

Imports System.Runtime.InteropServices
Imports System.Text

Namespace Ui

    ''' <summary>
    ''' Desktop notifications for the "notifications" setting: "auto" (the default) rings the bell and, when the terminal is in
    ''' the background, shows a Windows notification (notify-send on Linux); "bell" only rings; "off" does neither. Fire and
    ''' forget: a notification that cannot be shown is simply lost.
    ''' </summary>
    Friend NotInheritable Class DesktopNotify

        Private Sub New()
        End Sub

        <DllImport("user32.dll")>
        Private Shared Function GetForegroundWindow() As IntPtr
        End Function
        <DllImport("kernel32.dll")>
        Private Shared Function GetConsoleWindow() As IntPtr
        End Function
        <DllImport("user32.dll")>
        Private Shared Function GetAncestor(hwnd As IntPtr, flags As UInteger) As IntPtr
        End Function
        <DllImport("user32.dll")>
        Private Shared Function GetWindow(hwnd As IntPtr, cmd As UInteger) As IntPtr
        End Function
        <DllImport("user32.dll")>
        Private Shared Function GetWindowThreadProcessId(hwnd As IntPtr, ByRef processId As UInteger) As UInteger
        End Function
        <DllImport("ntdll.dll")>
        Private Shared Function NtQueryInformationProcess(handle As IntPtr, infoClass As Integer, ByRef info As BasicInfo, size As Integer, ByRef returned As Integer) As Integer
        End Function

        <StructLayout(LayoutKind.Sequential)>
        Private Structure BasicInfo
            Public ExitStatus As IntPtr
            Public PebBaseAddress As IntPtr
            Public AffinityMask As IntPtr
            Public BasePriority As IntPtr
            Public UniqueProcessId As IntPtr
            Public InheritedFromUniqueProcessId As IntPtr
        End Structure

        ''' <summary>The bell, and a notification when the terminal is in the background (per the setting: auto, bell or off).</summary>
        Public Shared Sub Alert(mode As String, title As String, message As String)
            Dim m = If(mode, "auto").Trim().ToLowerInvariant()
            If m = "off" Then Return
            Out.Raw(ChrW(7).ToString())
            If (m = "auto" OrElse m = "toast" OrElse m = "desktop") AndAlso Not TerminalInFront() Then Show(title, message)
        End Sub

        ''' <summary>
        ''' Whether this terminal is the window in front: the console window itself, the terminal that owns it, or a window of one
        ''' of the processes this CLI runs under (Windows Terminal, VS Code). Unknown (other systems): False.
        ''' </summary>
        Public Shared Function TerminalInFront() As Boolean
            If Not OperatingSystem.IsWindows() Then Return False
            Try
                Dim fg = GetForegroundWindow()
                If fg = IntPtr.Zero Then Return False
                Dim con = GetConsoleWindow()
                If con <> IntPtr.Zero AndAlso (con = fg OrElse GetAncestor(con, 3UI) = fg OrElse GetWindow(con, 4UI) = fg) Then Return True
                Dim pid As UInteger
                GetWindowThreadProcessId(fg, pid)
                If pid = 0 Then Return False
                Dim current = Environment.ProcessId
                For depth = 0 To 12
                    If current = CInt(pid) Then Return True
                    current = ParentOf(current)
                    If current <= 0 Then Exit For
                Next
            Catch ex As Exception When TypeOf ex Is DllNotFoundException OrElse TypeOf ex Is EntryPointNotFoundException OrElse TypeOf ex Is ComponentModel.Win32Exception
            End Try
            Return False
        End Function

        Private Shared Function ParentOf(pid As Integer) As Integer
            Try
                Using p = Process.GetProcessById(pid)
                    Dim info As New BasicInfo()
                    Dim returned As Integer
                    If NtQueryInformationProcess(p.Handle, 0, info, Marshal.SizeOf(info), returned) <> 0 Then Return 0
                    Return CInt(info.InheritedFromUniqueProcessId.ToInt64())
                End Using
            Catch ex As Exception When TypeOf ex Is ArgumentException OrElse TypeOf ex Is InvalidOperationException OrElse TypeOf ex Is ComponentModel.Win32Exception OrElse TypeOf ex Is NotSupportedException
                Return 0
            End Try
        End Function

        ''' <summary>Shows a notification: Windows' own (through Windows PowerShell), or notify-send on Linux.</summary>
        Public Shared Sub Show(title As String, message As String)
            Dim t = Shorten(If(String.IsNullOrWhiteSpace(title), "Agentic", title), 80)
            Dim m = Shorten(If(message, ""), 200)
            Try
                Dim psi As ProcessStartInfo
                If OperatingSystem.IsWindows() Then
                    psi = New ProcessStartInfo("powershell.exe")
                    For Each a In {"-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(ToastScript(t, m)))}
                        psi.ArgumentList.Add(a)
                    Next
                Else
                    Dim exe = Core.Platform.Which("notify-send")
                    If exe Is Nothing Then Return
                    psi = New ProcessStartInfo(exe)
                    psi.ArgumentList.Add("--app-name=Agentic")
                    psi.ArgumentList.Add(t)
                    psi.ArgumentList.Add(m)
                End If
                psi.UseShellExecute = False
                psi.CreateNoWindow = True
                psi.RedirectStandardOutput = True
                psi.RedirectStandardError = True
                Dim p = Process.Start(psi)
                If p Is Nothing Then Return
                ' Not waited for: the notification shows on its own; its output is read so the process never blocks on it.
                Dim drain = Task.Run(Async Function()
                                         Try
                                             Await p.StandardOutput.ReadToEndAsync().ConfigureAwait(False)
                                             Await p.StandardError.ReadToEndAsync().ConfigureAwait(False)
                                             Await p.WaitForExitAsync().ConfigureAwait(False)
                                         Catch ex As Exception When TypeOf ex Is InvalidOperationException OrElse TypeOf ex Is IO.IOException
                                         Finally
                                             p.Dispose()
                                         End Try
                                     End Function)
            Catch ex As Exception When TypeOf ex Is ComponentModel.Win32Exception OrElse TypeOf ex Is InvalidOperationException OrElse TypeOf ex Is IO.IOException
            End Try
        End Sub

        ''' <summary>
        ''' The PowerShell that shows a Windows notification. It uses Windows PowerShell's own app id, which every Windows 10 and
        ''' 11 has registered, so no shortcut of our own is needed.
        ''' </summary>
        Friend Shared Function ToastScript(title As String, message As String) As String
            Dim xml = "<toast><visual><binding template=""ToastGeneric""><text>" & Security.SecurityElement.Escape(title) & "</text><text>" &
                      Security.SecurityElement.Escape(message) & "</text></binding></visual></toast>"
            Return "$ErrorActionPreference='Stop';" &
                   "[Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] | Out-Null;" &
                   "[Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom.XmlDocument, ContentType = WindowsRuntime] | Out-Null;" &
                   "$x = New-Object Windows.Data.Xml.Dom.XmlDocument; $x.LoadXml('" & xml.Replace("'", "''") & "');" &
                   "[Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\WindowsPowerShell\v1.0\powershell.exe').Show([Windows.UI.Notifications.ToastNotification]::new($x))"
        End Function

        Private Shared Function Shorten(value As String, max As Integer) As String
            Dim s = RegularExpressions.Regex.Replace(If(value, ""), "\s+", " ").Trim()
            Return If(s.Length > max, s.Substring(0, max - 1) & "…", s)
        End Function

    End Class

End Namespace
