Option Strict On
Option Explicit On
Option Infer On

Imports System.IO
Imports System.Net.Http
Imports System.Text
Imports System.Text.Json
Imports System.Threading

Namespace Core

    ''' <summary>An editor that offers its IDE bridge: Agentic for VS Code writes ~\.agentic\ide\&lt;port&gt;.lock while it runs.</summary>
    Friend NotInheritable Class IdeInfo
        Public Property Port As Integer
        Public Property Token As String
        Public Property IdeName As String
        Public Property Version As String
        Public Property Pid As Integer
        Public Property Folders As New List(Of String)
        Public Property LockFile As String
        ''' <summary>The workspace folder that holds the CLI's folder (Nothing when none does).</summary>
        Public Property MatchedFolder As String

        Public ReadOnly Property Label As String
            Get
                Return $"{IdeName} · {If(MatchedFolder, If(Folders.FirstOrDefault(), "no folder"))}"
            End Get
        End Property
    End Class

    ''' <summary>
    ''' /ide: the CLI connected to VS Code (Agentic for VS Code's IDE bridge, like Claude Code's IDE integration). The CLI
    ''' sees the file open in the editor and the selected lines (they go with each message), asks for the problems VS Code
    ''' reports (the IdeDiagnostics tool), and shows file edits as diffs in VS Code to accept, reject or accept in part.
    ''' </summary>
    Friend NotInheritable Class IdeLink

        Private Shared ReadOnly Http As New HttpClient With {.Timeout = Timeout.InfiniteTimeSpan}
        Public ReadOnly Property Info As IdeInfo
        ''' <summary>What the last message said about the editor (the same again is not repeated).</summary>
        Public Property LastSent As String

        Public Sub New(info As IdeInfo)
            Me.Info = info
        End Sub

        Public Shared Function LockDir() As String
            Dim custom = Environment.GetEnvironmentVariable("AGENTIC_IDE_DIR")
            Return If(String.IsNullOrWhiteSpace(custom), Path.Combine(Paths.Home(), ".agentic", "ide"), custom)
        End Function

        Private Shared Function Alive(pid As Integer) As Boolean
            If pid <= 0 Then Return True
            Try
                Using p = Process.GetProcessById(pid)
                    Return Not p.HasExited
                End Using
            Catch ex As Exception When TypeOf ex Is ArgumentException OrElse TypeOf ex Is InvalidOperationException OrElse TypeOf ex Is ComponentModel.Win32Exception
                Return False
            End Try
        End Function

        Private Shared Function Inside(folder As String, dir As String) As Boolean
            Try
                Dim f = Path.GetFullPath(folder).TrimEnd("\"c, "/"c)
                Dim d = Path.GetFullPath(dir).TrimEnd("\"c, "/"c)
                Dim cmp = If(OperatingSystem.IsWindows(), StringComparison.OrdinalIgnoreCase, StringComparison.Ordinal)
                Return d.Equals(f, cmp) OrElse d.StartsWith(f & Path.DirectorySeparatorChar, cmp)
            Catch ex As Exception When TypeOf ex Is ArgumentException OrElse TypeOf ex Is NotSupportedException
                Return False
            End Try
        End Function

        ''' <summary>The editors running now, those whose workspace holds dir first (the closest folder first).</summary>
        Public Shared Function Available(dir As String) As List(Of IdeInfo)
            Dim list As New List(Of IdeInfo)
            Dim root = LockDir()
            If Not Directory.Exists(root) Then Return list
            For Each f In Directory.EnumerateFiles(root, "*.lock")
                Try
                    Using doc = JsonDocument.Parse(File.ReadAllText(f))
                        Dim r = doc.RootElement
                        Dim info As New IdeInfo With {.Port = Jx.IntOf(r, "port"), .Token = Jx.StrOf(r, "token"), .IdeName = Jx.StrOf(r, "ideName", "VS Code"),
                                                      .Version = Jx.StrOf(r, "version"), .Pid = Jx.IntOf(r, "pid"), .Folders = Jx.StringsOf(r, "workspaceFolders").ToList(), .LockFile = f}
                        If info.Port <= 0 OrElse String.IsNullOrEmpty(info.Token) Then Continue For
                        If Not Alive(info.Pid) Then
                            Try
                                File.Delete(f)
                            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
                            End Try
                            Continue For
                        End If
                        info.MatchedFolder = info.Folders.Where(Function(x) Inside(x, dir)).OrderByDescending(Function(x) x.Length).FirstOrDefault()
                        list.Add(info)
                    End Using
                Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is JsonException OrElse TypeOf ex Is UnauthorizedAccessException
                End Try
            Next
            Return list.OrderByDescending(Function(i) If(i.MatchedFolder?.Length, -1)).ToList()
        End Function

        Private Async Function SendAsync(method As String, route As String, body As Object, timeoutSeconds As Integer, ct As CancellationToken) As Task(Of JsonElement)
            Using cts = CancellationTokenSource.CreateLinkedTokenSource(ct)
                If timeoutSeconds > 0 Then cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds))
                Using req As New HttpRequestMessage(New HttpMethod(method), $"http://127.0.0.1:{Info.Port}{route}")
                    req.Headers.Authorization = New Headers.AuthenticationHeaderValue("Bearer", Info.Token)
                    If body IsNot Nothing Then req.Content = New StringContent(Jx.Stringify(body), Encoding.UTF8, "application/json")
                    Using resp = Await Http.SendAsync(req, cts.Token).ConfigureAwait(False)
                        Dim text = Await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(False)
                        If Not resp.IsSuccessStatusCode Then Throw New IOException($"{Info.IdeName} answered {CInt(resp.StatusCode)}: {ToolsText(text)}")
                        Return Jx.Parse(If(String.IsNullOrWhiteSpace(text), "{}", text))
                    End Using
                End Using
            End Using
        End Function

        Private Shared Function ToolsText(s As String) As String
            Return If(s Is Nothing, "", If(s.Length > 200, s.Substring(0, 200), s))
        End Function

        ''' <summary>The workspace folders, the file in the editor and the selection (with its lines and text).</summary>
        Public Function StateAsync(ct As CancellationToken) As Task(Of JsonElement)
            Return SendAsync("GET", "/state", Nothing, 4, ct)
        End Function

        Public Function DiagnosticsAsync(filePath As String, ct As CancellationToken) As Task(Of JsonElement)
            Return SendAsync("GET", "/diagnostics" & If(String.IsNullOrEmpty(filePath), "", "?path=" & Uri.EscapeDataString(filePath)), Nothing, 10, ct)
        End Function

        Public Function OpenAsync(filePath As String, line As Integer, ct As CancellationToken) As Task(Of JsonElement)
            Return SendAsync("POST", "/open", New With {.path = filePath, .line = line}, 10, ct)
        End Function

        ''' <summary>Shows the change as a diff in the editor and waits for the user: accepted, and the text as they left it.</summary>
        Public Async Function DiffAsync(filePath As String, content As String, ct As CancellationToken) As Task(Of (Accepted As Boolean, Content As String))
            Dim r = Await SendAsync("POST", "/diff", New With {.path = filePath, .content = content}, 0, ct).ConfigureAwait(False)
            Dim c As JsonElement
            Dim text = If(r.TryGetProperty("content", c) AndAlso c.ValueKind = JsonValueKind.String, c.GetString(), Nothing)
            Return (Jx.BoolOf(r, "accepted", False), text)
        End Function

        ''' <summary>
        ''' What goes with a message about the editor: the selected lines, else the file open in it (only when it changed since
        ''' the last message). Nothing when there is nothing new or the editor does not answer.
        ''' </summary>
        Public Async Function ContextAsync(display As Func(Of String, String), ct As CancellationToken) As Task(Of String)
            Dim s As JsonElement
            Try
                s = Await StateAsync(ct).ConfigureAwait(False)
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is HttpRequestException OrElse TypeOf ex Is OperationCanceledException OrElse TypeOf ex Is JsonException
                Return Nothing
            End Try
            Dim note As String = Nothing
            Dim sel As JsonElement
            If s.TryGetProperty("selection", sel) AndAlso sel.ValueKind = JsonValueKind.Object Then
                Dim p = display(Jx.StrOf(sel, "path"))
                Dim a = Jx.IntOf(sel, "startLine"), b = Jx.IntOf(sel, "endLine")
                note = $"<ide-selection file=""{p}"" lines=""{a}-{b}"">{vbLf}The user selected these lines in {Info.IdeName}:{vbLf}{Jx.StrOf(sel, "text")}{vbLf}</ide-selection>"
            Else
                Dim active = Jx.StrOf(s, "activeFile")
                If active.Length > 0 Then note = $"<ide-opened-file>The user has {display(active)} open in {Info.IdeName}. It may or may not be related to the task.</ide-opened-file>"
            End If
            If note Is Nothing OrElse note = LastSent Then Return Nothing
            LastSent = note
            Return note
        End Function

    End Class

End Namespace
