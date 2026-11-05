Option Strict On
Option Explicit On
Option Infer On

Imports System.IO
Imports System.Text

Namespace Core

    ''' <summary>
    ''' Where the CLI keeps things:
    '''   ~\.agentic\               settings.json, mcp.json, AGENTS.md (your memory), skills\, commands\, agents\
    '''   ~\.agentic\projects\&lt;p&gt;\  per project: sessions, input history, checkpoints
    '''   &lt;exe folder&gt;\config.json  serverBaseUrl and apiKey
    '''   %APPDATA%\AgenticCli\     secrets.bin (where older versions kept them, DPAPI-protected; still read)
    '''   &lt;project&gt;\.agentic\      settings.json (shared), settings.local.json (yours), skills\, commands\, agents\
    ''' </summary>
    Friend NotInheritable Class Paths

        ''' <summary>Self-tests: another home folder (Claude Code's files are read from it).</summary>
        Friend Shared Property HomeOverride As String

        Public Shared Function Home() As String
            If Not String.IsNullOrEmpty(HomeOverride) Then Return HomeOverride
            Return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        End Function

        Public Shared Function AgenticHome() As String
            Dim custom = Environment.GetEnvironmentVariable("AGENTIC_HOME")
            Return EnsureDir(If(String.IsNullOrWhiteSpace(custom), Path.Combine(Home(), ".agentic"), custom))
        End Function

        Public Shared Function ClaudeHome() As String
            Return Path.Combine(Home(), ".claude")
        End Function

        Public Shared Function UserSettingsFile() As String
            Return Path.Combine(AgenticHome(), "settings.json")
        End Function

        Public Shared Function UserMcpFile() As String
            Return Path.Combine(AgenticHome(), "mcp.json")
        End Function

        Public Shared Function UserMemoryFile() As String
            Return Path.Combine(AgenticHome(), "AGENTS.md")
        End Function

        Public Shared Function CredentialsFile() As String
            Dim custom = Environment.GetEnvironmentVariable("AGENTIC_HOME")
            Dim dir = If(String.IsNullOrWhiteSpace(custom), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AgenticCli"), custom)
            Return Path.Combine(EnsureDir(dir), "secrets.bin")
        End Function

        ''' <summary>A folder name for a project path, like Claude Code's: C:\Users\me\app becomes C--Users-me-app.</summary>
        Public Shared Function ProjectKey(dir As String) As String
            Dim sb As New StringBuilder()
            For Each c In Path.GetFullPath(dir).TrimEnd("\"c, "/"c)
                sb.Append(If(Char.IsLetterOrDigit(c) OrElse c = "."c OrElse c = "_"c, c, "-"c))
            Next
            Dim key = sb.ToString()
            Return If(key.Length > 120, key.Substring(key.Length - 120), key)
        End Function

        Public Shared Function ProjectDataDir(dir As String) As String
            Return EnsureDir(Path.Combine(AgenticHome(), "projects", ProjectKey(dir)))
        End Function

        Public Shared Function EnsureDir(dir As String) As String
            Try
                Directory.CreateDirectory(dir)
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
            End Try
            Return dir
        End Function

        Public Shared Function ReadText(file As String) As String
            Try
                If String.IsNullOrEmpty(file) OrElse Not IO.File.Exists(file) Then Return Nothing
                Return IO.File.ReadAllText(file)
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
                Return Nothing
            End Try
        End Function

        ''' <summary>Writes through a temporary file so a crash never leaves half a file.</summary>
        Public Shared Sub WriteText(file As String, text As String)
            Dim dir = Path.GetDirectoryName(file)
            If Not String.IsNullOrEmpty(dir) Then Directory.CreateDirectory(dir)
            Dim tmp = file & ".tmp-" & Environment.ProcessId
            IO.File.WriteAllText(tmp, text, New UTF8Encoding(False))
            IO.File.Move(tmp, file, True)
        End Sub

        Public Shared Function Full(p As String, Optional baseDir As String = Nothing) As String
            If String.IsNullOrWhiteSpace(p) Then Return Path.GetFullPath(If(baseDir, Environment.CurrentDirectory))
            Dim s = p.Trim().Trim(""""c)
            If s = "~" Then s = Home()
            If s.StartsWith("~/", StringComparison.Ordinal) OrElse s.StartsWith("~\", StringComparison.Ordinal) Then s = Path.Combine(Home(), s.Substring(2))
            s = Environment.ExpandEnvironmentVariables(s)
            If Not Path.IsPathRooted(s) Then s = Path.Combine(If(baseDir, Environment.CurrentDirectory), s)
            Return Path.GetFullPath(s)
        End Function

        Public Shared Function IsInside(p As String, root As String) As Boolean
            If String.IsNullOrEmpty(p) OrElse String.IsNullOrEmpty(root) Then Return False
            Dim a = Path.GetFullPath(p).TrimEnd("\"c, "/"c)
            Dim b = Path.GetFullPath(root).TrimEnd("\"c, "/"c)
            Return a.Equals(b, StringComparison.OrdinalIgnoreCase) OrElse a.StartsWith(b & Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
        End Function

        ''' <summary>A short form for display: relative to the working directory when inside it, ~ for the home folder.</summary>
        Public Shared Function Display(p As String, cwd As String) As String
            If String.IsNullOrEmpty(p) Then Return ""
            Try
                Dim f = Path.GetFullPath(p)
                If Not String.IsNullOrEmpty(cwd) AndAlso IsInside(f, cwd) Then
                    Dim rel = Path.GetRelativePath(cwd, f)
                    Return If(rel = ".", ".", rel)
                End If
                Dim h = Home()
                If IsInside(f, h) Then Return "~" & f.Substring(h.TrimEnd("\"c).Length)
                Return f
            Catch ex As Exception When TypeOf ex Is ArgumentException OrElse TypeOf ex Is NotSupportedException OrElse TypeOf ex Is PathTooLongException
                Return p
            End Try
        End Function

        ''' <summary>Forward slashes, for glob matching and for display in rules.</summary>
        Public Shared Function Slashes(p As String) As String
            Return If(p, "").Replace("\"c, "/"c)
        End Function

    End Class

End Namespace
