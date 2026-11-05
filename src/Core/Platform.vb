Option Strict On
Option Explicit On
Option Infer On

Imports System.IO
Imports System.Runtime.InteropServices

Namespace Core

    ''' <summary>
    ''' What differs between Windows and Linux: the shell that runs command lines, finding programs on PATH, the update
    ''' channel of this build and the file mode of a swapped-in binary.
    ''' </summary>
    Friend NotInheritable Class Platform

        Private Sub New()
        End Sub

        ''' <summary>"win-x64", "linux-x64", "linux-arm64", "osx-arm64" …: the platform this build runs on.</summary>
        Public Shared Function Rid() As String
            Dim os = If(OperatingSystem.IsWindows(), "win", If(OperatingSystem.IsMacOS(), "osx", "linux"))
            Dim arch = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant()
            Return os & "-" & arch
        End Function

        ''' <summary>
        ''' The server's update channel for this build: the app itself on Windows ("cli"), the app and platform elsewhere
        ''' ("cli-linux-x64"), so each platform gets its own build.
        ''' </summary>
        Public Shared Function UpdateApp(baseApp As String) As String
            Return If(OperatingSystem.IsWindows(), baseApp, baseApp & "-" & Rid())
        End Function

        ''' <summary>A command line for the platform shell: cmd /d /s /c on Windows, /bin/sh -c elsewhere.</summary>
        Public Shared Function ShellStart(commandLine As String) As ProcessStartInfo
            Dim psi As ProcessStartInfo
            If OperatingSystem.IsWindows() Then
                psi = New ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
                psi.ArgumentList.Add("/d")
                psi.ArgumentList.Add("/s")
                psi.ArgumentList.Add("/c")
            Else
                psi = New ProcessStartInfo("/bin/sh")
                psi.ArgumentList.Add("-c")
            End If
            psi.ArgumentList.Add(commandLine)
            psi.UseShellExecute = False
            Return psi
        End Function

        ''' <summary>
        ''' A program on PATH, or Nothing. Windows tries the PATHEXT endings (.exe, .cmd …); elsewhere the name itself.
        ''' A name with a folder in it is checked as it is.
        ''' </summary>
        Public Shared Function Which(program As String) As String
            If String.IsNullOrWhiteSpace(program) Then Return Nothing
            Dim candidates As New List(Of String)
            If OperatingSystem.IsWindows() Then
                Dim exts = If(Environment.GetEnvironmentVariable("PATHEXT"), ".COM;.EXE;.BAT;.CMD").Split(";"c, StringSplitOptions.RemoveEmptyEntries)
                If Path.HasExtension(program) Then candidates.Add(program)
                candidates.AddRange(exts.Select(Function(e) program & e.ToLowerInvariant()))
            Else
                candidates.Add(program)
            End If
            If program.Contains("\"c) OrElse program.Contains("/"c) Then
                Return candidates.FirstOrDefault(Function(c) IsRunnable(c))
            End If
            For Each folder As String In If(Environment.GetEnvironmentVariable("PATH"), "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                For Each c In candidates
                    Try
                        Dim full = Path.Combine(folder.Trim(""""c), c)
                        If IsRunnable(full) Then Return full
                    Catch ex As ArgumentException
                    End Try
                Next
            Next
            Return Nothing
        End Function

        ''' <summary>An existing file that this user may run (any file on Windows).</summary>
        Private Shared Function IsRunnable(file As String) As Boolean
            If Not IO.File.Exists(file) Then Return False
            If OperatingSystem.IsWindows() Then Return True
            Try
                Return (IO.File.GetUnixFileMode(file) And (UnixFileMode.UserExecute Or UnixFileMode.GroupExecute Or UnixFileMode.OtherExecute)) <> 0
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
                Return False
            End Try
        End Function

        ''' <summary>Gives target the file mode of model, executable (a binary swapped in by an update); nothing on Windows.</summary>
        Public Shared Sub CopyFileMode(model As String, target As String)
            If OperatingSystem.IsWindows() Then Return
            Dim mode = UnixFileMode.UserRead Or UnixFileMode.UserWrite Or UnixFileMode.UserExecute Or UnixFileMode.GroupRead Or
                       UnixFileMode.GroupExecute Or UnixFileMode.OtherRead Or UnixFileMode.OtherExecute
            Try
                If IO.File.Exists(model) Then mode = IO.File.GetUnixFileMode(model) Or UnixFileMode.UserExecute
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
            End Try
            IO.File.SetUnixFileMode(target, mode)
        End Sub

    End Class

End Namespace
