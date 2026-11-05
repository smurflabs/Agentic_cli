Option Strict On
Option Explicit On
Option Infer On

Imports System.IO
Imports System.Text
Imports System.Text.RegularExpressions
Imports System.Threading
Imports AgenticCli.Api
Imports AgenticCli.Core
Imports AgenticCli.Ui

Namespace Agent

    ' /research, /simplify, /verify and /worktree; Ctrl+G (the message in your editor) and Alt+V (an image from the clipboard).
    Partial Friend NotInheritable Class Session

        ''' <summary>The next message is researched in depth on the server (/research).</summary>
        Private _researchNext As Boolean
        ''' <summary>Images pasted with Alt+V, by the number in the message box's [Image #n].</summary>
        Private ReadOnly _pastedImages As New Dictionary(Of Integer, String)

        Private Const SimplifyPrompt As String =
            "Look at the pending changes (git status, git diff and git diff --cached) and simplify them without changing what they do: remove duplication " &
            "and dead code, use helpers that already exist in the project, choose clearer names, flatten needless nesting and match the style of the code " &
            "around them. Make the edits, build or run the tests if the project has them, then list briefly what you changed and why."

        Private Const VerifyPrompt As String =
            "Check that the latest changes actually work. Find out how this project is built, run and tested (README, package and build files, scripts), " &
            "build it, run the tests that cover the change, and if it is an app, start it and try the changed feature. Report exactly what you ran, what " &
            "passed and what failed with the exact errors. Do not fix anything unless I ask; when something fails, say what you think causes it."

        ''' <summary>/research: the server researches the question in depth (web search on, several searches, sources cross-checked and cited).</summary>
        Private Async Function ResearchAsync(question As String) As Task
            If question.Length = 0 Then
                Out.Fail("Usage: /research <question>")
                Return
            End If
            If Not _meta.WebSearch Then
                Out.Warning("The server has no web search: the model researches from what it knows.")
            ElseIf Not _server.WebSearch Then
                _server.WebSearch = True
                Out.Note("Web search is on for this session (/web off turns it off).")
            End If
            _researchNext = True
            Await SendAsync(question).ConfigureAwait(False)
        End Function

        ''' <summary>/worktree [name | new &lt;name&gt; | list | done | remove &lt;name&gt; [--force]].</summary>
        Private Sub WorktreeCommand(arg As String)
            Dim parts = arg.Split({" "c}, 2, StringSplitOptions.RemoveEmptyEntries)
            Dim verb = If(parts.Length > 0, parts(0).ToLowerInvariant(), "")
            Dim rest = If(parts.Length > 1, parts(1).Trim(), "")
            Select Case verb
                Case "", "list"
                    Dim list = Worktrees.List(State.Cwd)
                    If list Is Nothing Then
                        Out.Fail("This folder is not in a git repository.")
                        Return
                    End If
                    Out.Line(list)
                    Out.Line(Ansi.Muted("/worktree <name> works in a new worktree · /worktree done goes back · /worktree remove <name> deletes one"))
                Case "done", "exit", "back"
                    Dim root = Worktrees.MainRoot(State.Cwd)
                    If root Is Nothing Then
                        Out.Fail("This folder is not in a git repository.")
                        Return
                    End If
                    ChangeDir(root)
                Case "remove", "rm", "delete"
                    Dim force = Regex.IsMatch(rest, "(^|\s)--force(\s|$)")
                    Dim name = Regex.Replace(rest, "(^|\s)--force(\s|$)", " ").Trim()
                    If name.Length = 0 Then
                        Out.Fail("Usage: /worktree remove <name> [--force]")
                        Return
                    End If
                    Dim root = Worktrees.MainRoot(State.Cwd)
                    ' Leave the worktree first when the session is in it.
                    If root IsNot Nothing AndAlso Paths.IsInside(State.Cwd, Worktrees.FolderOf(root, Worktrees.NameFrom(name))) Then ChangeDir(root)
                    Dim failure = Worktrees.Remove(If(root, State.Cwd), name, force)
                    If failure Is Nothing Then
                        Out.Success($"Removed the worktree {Worktrees.NameFrom(name)}" & Ansi.Muted($" (its branch agentic/{Worktrees.NameFrom(name)} stays; git branch -D deletes it)"))
                    Else
                        Out.Fail(failure & If(force, "", " — /worktree remove " & Worktrees.NameFrom(name) & " --force removes it with its changes"))
                    End If
                Case Else
                    Dim name = If(verb = "new" OrElse verb = "add" OrElse verb = "create", rest, arg)
                    Dim wt = Worktrees.Create(State.Cwd, name)
                    If wt.Error IsNot Nothing Then
                        Out.Fail("Could not make the worktree: " & wt.Error)
                        Return
                    End If
                    ChangeDir(wt.Folder)
                    Out.Line(Ansi.Muted($"  Branch {wt.Branch}: changes here stay out of the main checkout until you merge it. /worktree done goes back."))
            End Select
        End Sub

        ''' <summary>Ctrl+G: the message in your editor ($VISUAL, $EDITOR, else Notepad or nano); what you save comes back into the box.</summary>
        Private Function EditMessageInEditor(text As String) As String
            Dim file = Path.Combine(Path.GetTempPath(), $"agentic-message-{Environment.ProcessId}.md")
            Try
                IO.File.WriteAllText(file, If(text, ""))
                If Not OpenInEditor(file) Then Return Nothing
                Return IO.File.ReadAllText(file).Replace(vbCrLf, vbLf)
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
                Out.Fail("Could not edit the message: " & ex.Message)
                Return Nothing
            Finally
                Try
                    IO.File.Delete(file)
                Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
                End Try
            End Try
        End Function

        ''' <summary>Alt+V: the clipboard's image saved as a PNG; the box gets [Image #n], and the image is attached when the message is sent.</summary>
        Private Function PasteClipboardImage() As String
            Dim n = _pastedImages.Count + 1
            Dim file = Path.Combine(Path.GetTempPath(), $"agentic-paste-{Environment.ProcessId}-{n}.png")
            If Not ClipboardImage.Save(file) Then Return Nothing
            _pastedImages(n) = file
            Return $"[Image #{n}]"
        End Function

        ''' <summary>Uploads the pasted images a message mentions ([Image #n]); the placeholders stay in the text, so the model knows which is which.</summary>
        Private Async Function AttachPastedImagesAsync(prompt As String, attachments As List(Of String)) As Task
            Dim done As New HashSet(Of Integer)
            For Each m As Match In Regex.Matches(If(prompt, ""), "\[Image #(\d+)\]")
                Dim n As Integer
                If Not Integer.TryParse(m.Groups(1).Value, n) OrElse Not done.Add(n) Then Continue For
                Dim file As String = Nothing
                If Not _pastedImages.TryGetValue(n, file) OrElse Not IO.File.Exists(file) Then Continue For
                Try
                    Dim up = Await _chat.UploadAsync($"image-{n}.png", IO.File.ReadAllBytes(file), CancellationToken.None).ConfigureAwait(False)
                    attachments.Add(up.Id)
                    Out.Line(Ansi.Muted($"  {Ansi.Elbow}  Attached [Image #{n}]"))
                Catch ex As Exception When TypeOf ex Is ApiException OrElse TypeOf ex Is IOException
                    Out.Warning($"Could not attach [Image #{n}]: {ex.Message}")
                End Try
            Next
        End Function

        ''' <summary>The pasted images' temporary files, removed when the session ends.</summary>
        Private Sub DeletePastedImages()
            For Each file In _pastedImages.Values
                Try
                    IO.File.Delete(file)
                Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
                End Try
            Next
            _pastedImages.Clear()
        End Sub

    End Class

    ''' <summary>
    ''' The clipboard's image as a PNG file: a screenshot, a copied picture, or a copied image file. Windows: through Windows
    ''' PowerShell (System.Windows.Forms reads every image format Windows puts there); Linux: wl-paste or xclip.
    ''' </summary>
    Friend NotInheritable Class ClipboardImage

        Private Sub New()
        End Sub

        Public Shared Function Save(file As String) As Boolean
            Try
                If IO.File.Exists(file) Then IO.File.Delete(file)
                If OperatingSystem.IsWindows() Then Return SaveWindows(file)
                Return SaveUnix(file)
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException OrElse
                                       TypeOf ex Is ComponentModel.Win32Exception OrElse TypeOf ex Is InvalidOperationException
                Return False
            End Try
        End Function

        Private Shared Function SaveWindows(file As String) As Boolean
            Dim script = "Add-Type -AssemblyName System.Windows.Forms, System.Drawing;" &
                         "$i = [System.Windows.Forms.Clipboard]::GetImage();" &
                         "if ($i -eq $null) { $f = [System.Windows.Forms.Clipboard]::GetFileDropList();" &
                         " if ($f.Count -gt 0 -and $f[0] -match '\.(png|jpe?g|gif|bmp|webp|tiff?)$') { try { $i = [System.Drawing.Image]::FromFile($f[0]) } catch { } } }" &
                         "if ($i -eq $null) { exit 3 }" &
                         "$i.Save('" & file.Replace("'", "''") & "', [System.Drawing.Imaging.ImageFormat]::Png); exit 0"
            Dim psi As New ProcessStartInfo("powershell.exe") With {.UseShellExecute = False, .CreateNoWindow = True, .RedirectStandardOutput = True, .RedirectStandardError = True}
            ' -STA: the clipboard needs a single-threaded apartment.
            For Each a In {"-NoProfile", "-NonInteractive", "-STA", "-ExecutionPolicy", "Bypass", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script))}
                psi.ArgumentList.Add(a)
            Next
            Using p = Process.Start(psi)
                Dim drainOut = p.StandardOutput.ReadToEndAsync()
                Dim drainErr = p.StandardError.ReadToEndAsync()
                If Not p.WaitForExit(20000) Then
                    p.Kill(True)
                    Return False
                End If
                Return p.ExitCode = 0 AndAlso IO.File.Exists(file) AndAlso New FileInfo(file).Length > 0
            End Using
        End Function

        Private Shared Function SaveUnix(file As String) As Boolean
            Dim tools As New List(Of (Exe As String, Args As String()))
            If Not String.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")) Then tools.Add(("wl-paste", {"--no-newline", "--type", "image/png"}))
            If Not String.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")) Then tools.Add(("xclip", {"-selection", "clipboard", "-t", "image/png", "-o"}))
            For Each t In tools
                Dim exe = Platform.Which(t.Exe)
                If exe Is Nothing Then Continue For
                Dim psi As New ProcessStartInfo(exe) With {.UseShellExecute = False, .RedirectStandardOutput = True, .RedirectStandardError = True}
                For Each a In t.Args
                    psi.ArgumentList.Add(a)
                Next
                Dim ok = False
                Using p = Process.Start(psi), fs = IO.File.Create(file)
                    Dim copy = p.StandardOutput.BaseStream.CopyToAsync(fs)
                    Dim drainErr = p.StandardError.ReadToEndAsync()
                    If p.WaitForExit(10000) Then
                        copy.Wait(5000)
                        ok = p.ExitCode = 0
                    Else
                        p.Kill(True)
                    End If
                End Using
                ' A PNG starts with its signature; anything else (no image on the clipboard) is not one.
                If ok AndAlso IsPng(file) Then Return True
            Next
            If IO.File.Exists(file) Then IO.File.Delete(file)
            Return False
        End Function

        Private Shared Function IsPng(file As String) As Boolean
            Dim head(7) As Byte
            Using fs = IO.File.OpenRead(file)
                If fs.Read(head, 0, 8) < 8 Then Return False
            End Using
            Return head(0) = &H89 AndAlso head(1) = &H50 AndAlso head(2) = &H4E AndAlso head(3) = &H47
        End Function

    End Class

End Namespace
