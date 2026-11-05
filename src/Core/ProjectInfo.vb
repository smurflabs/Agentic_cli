Option Strict On
Option Explicit On
Option Infer On

Imports System.IO
Imports System.Text

Namespace Core

    ''' <summary>The working directory and its git repository (branch from .git\HEAD, status from git when installed).</summary>
    Friend NotInheritable Class ProjectInfo

        Public Property Cwd As String
        Public Property GitRoot As String

        Public Shared Function Detect(cwd As String) As ProjectInfo
            Dim p As New ProjectInfo With {.Cwd = Path.GetFullPath(cwd)}
            Dim dir = New DirectoryInfo(p.Cwd)
            While dir IsNot Nothing
                Dim git = Path.Combine(dir.FullName, ".git")
                If Directory.Exists(git) OrElse File.Exists(git) Then
                    p.GitRoot = dir.FullName
                    Exit While
                End If
                dir = dir.Parent
            End While
            Return p
        End Function

        Public ReadOnly Property IsGit As Boolean
            Get
                Return GitRoot IsNot Nothing
            End Get
        End Property

        ''' <summary>The current branch (or a short commit id when detached); Nothing outside git.</summary>
        Public Function Branch() As String
            If GitRoot Is Nothing Then Return Nothing
            Try
                Dim gitDir = Path.Combine(GitRoot, ".git")
                If File.Exists(gitDir) Then
                    ' A worktree or submodule: ".git" is a file pointing at the real folder.
                    Dim line = File.ReadAllText(gitDir).Trim()
                    If line.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase) Then gitDir = Path.GetFullPath(Path.Combine(GitRoot, line.Substring(7).Trim()))
                End If
                Dim head = File.ReadAllText(Path.Combine(gitDir, "HEAD")).Trim()
                If head.StartsWith("ref: refs/heads/", StringComparison.Ordinal) Then Return head.Substring(16)
                Return If(head.Length >= 7, head.Substring(0, 7), head)
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
                Return Nothing
            End Try
        End Function

        ''' <summary>Runs git with arguments in the working directory; Nothing when git is missing or fails.</summary>
        Public Function Git(args As String, Optional timeoutMs As Integer = 4000) As String
            If GitRoot Is Nothing Then Return Nothing
            Return RunQuiet("git", args, Cwd, timeoutMs)
        End Function

        ''' <summary>A snapshot for the model: branch, short status and the last commits (trimmed).</summary>
        Public Function GitSnapshot() As String
            If GitRoot Is Nothing Then Return Nothing
            Dim sb As New StringBuilder()
            Dim b = Branch()
            If b IsNot Nothing Then sb.AppendLine("Current branch: " & b)
            Dim status = Git("status --short", 3000)
            If status IsNot Nothing Then
                Dim lines = status.Split({vbLf}, StringSplitOptions.RemoveEmptyEntries)
                sb.AppendLine("Status:" & If(lines.Length = 0, " (clean)", ""))
                For Each l In lines.Take(30)
                    sb.AppendLine(l.TrimEnd(ChrW(13)))
                Next
                If lines.Length > 30 Then sb.AppendLine($"… and {lines.Length - 30} more")
            End If
            Dim log = Git("log --oneline -5", 3000)
            If Not String.IsNullOrWhiteSpace(log) Then
                sb.AppendLine("Recent commits:")
                sb.Append(log.Trim())
            End If
            Return sb.ToString().Trim()
        End Function

        ''' <summary>Runs a program and returns its output (stdout), or Nothing on failure or timeout.</summary>
        Public Shared Function RunQuiet(fileName As String, args As String, workDir As String, timeoutMs As Integer) As String
            Try
                Dim psi As New ProcessStartInfo(fileName, args) With {
                    .UseShellExecute = False, .CreateNoWindow = True, .RedirectStandardOutput = True, .RedirectStandardError = True,
                    .WorkingDirectory = workDir, .StandardOutputEncoding = New UTF8Encoding(False)}
                Using p = Process.Start(psi)
                    Dim outTask = p.StandardOutput.ReadToEndAsync()
                    Dim errTask = p.StandardError.ReadToEndAsync()
                    If Not p.WaitForExit(timeoutMs) Then
                        Try
                            p.Kill(True)
                        Catch ex As InvalidOperationException
                        End Try
                        Return Nothing
                    End If
                    If p.ExitCode <> 0 Then Return Nothing
                    Return outTask.GetAwaiter().GetResult()
                End Using
            Catch ex As Exception When TypeOf ex Is ComponentModel.Win32Exception OrElse TypeOf ex Is InvalidOperationException OrElse TypeOf ex Is IOException
                Return Nothing
            End Try
        End Function

        ''' <summary>Looks a program up on PATH (with PATHEXT on Windows); Nothing when it is not installed.</summary>
        Public Shared Function Which(program As String) As String
            Return Platform.Which(program)
        End Function

    End Class

End Namespace
