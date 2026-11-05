Option Strict On
Option Explicit On
Option Infer On

Imports System.IO
Imports System.Text
Imports System.Text.RegularExpressions

Namespace Core

    ''' <summary>
    ''' Git worktrees for --worktree and /worktree (like Claude Code's): a separate folder on its own branch, so a session's
    ''' changes stay apart from the main checkout until you merge them. They live in .agentic\worktrees\&lt;name&gt; of the
    ''' main repository, on the branch agentic/&lt;name&gt;; git is told to ignore that folder (.git\info\exclude).
    ''' </summary>
    Friend NotInheritable Class Worktrees

        Private Sub New()
        End Sub

        ''' <summary>A name git accepts for the folder and branch, from what was typed; empty: one from the date and time.</summary>
        Public Shared Function NameFrom(text As String) As String
            Dim n = Regex.Replace(If(text, "").Trim(), "[^A-Za-z0-9._-]+", "-").Trim("-"c, "."c)
            If n.Length = 0 Then n = "session-" & DateTime.Now.ToString("MMdd-HHmmss", Globalization.CultureInfo.InvariantCulture)
            Return If(n.Length > 60, n.Substring(0, 60).TrimEnd("-"c, "."c), n)
        End Function

        ''' <summary>The main checkout's folder (not a worktree's), or Nothing outside git.</summary>
        Public Shared Function MainRoot(dir As String) As String
            Dim r = Git(dir, "rev-parse --path-format=absolute --git-common-dir")
            If Not r.Ok Then Return Nothing
            Dim common = r.Output.Trim()
            If common.Length = 0 Then Return Nothing
            ' The common .git folder sits in the main checkout (a bare repository has no checkout to work in).
            Return If(Path.GetFileName(common.TrimEnd("/"c, "\"c)).Equals(".git", StringComparison.OrdinalIgnoreCase), Path.GetDirectoryName(Path.GetFullPath(common.TrimEnd("/"c, "\"c))), Nothing)
        End Function

        Public Shared Function FolderOf(mainRoot As String, name As String) As String
            Return Path.Combine(mainRoot, ".agentic", "worktrees", name)
        End Function

        ''' <summary>
        ''' Creates the worktree &lt;name&gt; (branch agentic/&lt;name&gt; from the current commit), or reuses it when it is
        ''' there already. Returns its folder and branch, or what went wrong.
        ''' </summary>
        Public Shared Function Create(dir As String, name As String) As (Folder As String, Branch As String, [Error] As String)
            Dim root = MainRoot(dir)
            If root Is Nothing Then Return (Nothing, Nothing, "this folder is not in a git repository (a worktree needs git)")
            name = NameFrom(name)
            Dim folder = FolderOf(root, name)
            Dim branch = "agentic/" & name
            If Directory.Exists(folder) Then
                Dim current = Git(folder, "rev-parse --abbrev-ref HEAD")
                Return (folder, If(current.Ok, current.Output.Trim(), branch), Nothing)
            End If
            Exclude(root)
            Directory.CreateDirectory(Path.GetDirectoryName(folder))
            Dim r = Git(dir, $"worktree add -b ""{branch}"" ""{folder}"" HEAD")
            ' The branch is left from an earlier worktree of that name: check it out again.
            If Not r.Ok AndAlso r.Output.Contains("already exists") Then r = Git(dir, $"worktree add ""{folder}"" ""{branch}""")
            If Not r.Ok Then Return (Nothing, Nothing, FirstLine(r.Output))
            Return (folder, branch, Nothing)
        End Function

        ''' <summary>git worktree list, as git prints it.</summary>
        Public Shared Function List(dir As String) As String
            Dim r = Git(dir, "worktree list")
            Return If(r.Ok, r.Output.TrimEnd(), Nothing)
        End Function

        ''' <summary>Removes the worktree &lt;name&gt; (git refuses when it has changes, unless force); its branch stays.</summary>
        Public Shared Function Remove(dir As String, name As String, force As Boolean) As String
            Dim root = MainRoot(dir)
            If root Is Nothing Then Return "this folder is not in a git repository"
            Dim folder = FolderOf(root, NameFrom(name))
            If Not Directory.Exists(folder) Then Return $"there is no worktree {NameFrom(name)} in {Path.Combine(root, ".agentic", "worktrees")}"
            Dim r = Git(root, $"worktree remove {If(force, "--force ", "")}""{folder}""")
            Return If(r.Ok, Nothing, FirstLine(r.Output))
        End Function

        ''' <summary>Adds the worktrees folder to .git\info\exclude, so it never shows as new files in the main checkout.</summary>
        Private Shared Sub Exclude(root As String)
            Try
                Dim r = Git(root, "rev-parse --path-format=absolute --git-path info/exclude")
                If Not r.Ok Then Return
                Dim file = r.Output.Trim()
                Dim line = "/.agentic/worktrees/"
                Dim text = If(IO.File.Exists(file), IO.File.ReadAllText(file), "")
                If text.Split({vbLf, vbCr}, StringSplitOptions.RemoveEmptyEntries).Any(Function(l) l.Trim() = line) Then Return
                Directory.CreateDirectory(Path.GetDirectoryName(file))
                IO.File.AppendAllText(file, If(text.Length = 0 OrElse text.EndsWith(vbLf), "", vbLf) & line & vbLf)
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
            End Try
        End Sub

        Private Shared Function FirstLine(text As String) As String
            Dim lines = If(text, "").Split({vbLf, vbCr}, StringSplitOptions.RemoveEmptyEntries).Select(Function(l) l.Trim()).Where(Function(l) l.Length > 0).ToList()
            ' git's real complaint is the "fatal:" line when there is one.
            Return If(lines.FirstOrDefault(Function(l) l.StartsWith("fatal:", StringComparison.OrdinalIgnoreCase)), If(lines.FirstOrDefault(), "git failed"))
        End Function

        ''' <summary>Runs git in a folder: whether it succeeded, and what it printed (output and errors).</summary>
        Private Shared Function Git(dir As String, args As String) As (Ok As Boolean, Output As String)
            Try
                Dim psi As New ProcessStartInfo("git", args) With {
                    .UseShellExecute = False, .CreateNoWindow = True, .RedirectStandardOutput = True, .RedirectStandardError = True,
                    .WorkingDirectory = dir, .StandardOutputEncoding = New UTF8Encoding(False), .StandardErrorEncoding = New UTF8Encoding(False)}
                Using p = Process.Start(psi)
                    Dim outTask = p.StandardOutput.ReadToEndAsync()
                    Dim errTask = p.StandardError.ReadToEndAsync()
                    If Not p.WaitForExit(60000) Then
                        Try
                            p.Kill(True)
                        Catch ex As InvalidOperationException
                        End Try
                        Return (False, "git did not finish within a minute")
                    End If
                    Dim text = outTask.GetAwaiter().GetResult() & errTask.GetAwaiter().GetResult()
                    Return (p.ExitCode = 0, text)
                End Using
            Catch ex As Exception When TypeOf ex Is ComponentModel.Win32Exception OrElse TypeOf ex Is InvalidOperationException OrElse TypeOf ex Is IOException
                Return (False, "git is not installed or could not start: " & ex.Message)
            End Try
        End Function

    End Class

End Namespace
