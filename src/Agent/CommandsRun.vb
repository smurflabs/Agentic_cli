Option Strict On
Option Explicit On
Option Infer On

Imports System.IO
Imports System.Threading
Imports AgenticCli.Api
Imports AgenticCli.Core
Imports AgenticCli.Tools
Imports AgenticCli.Ui

Namespace Agent

    ' /run: run and test this session's projects like the desktop app's Run button and Claude Desktop's preview - a dev server
    ' (its address is printed once it answers; /run open shows it in the browser), the app, its tests (their output as it comes,
    ' then how they ended) - and share a project's website as a public link (/run share), as on the Artifacts page.
    Partial Friend NotInheritable Class Session

        Private _projectRuns As ProjectRuns
        Private Const RunHelp As String = "/run [name|#] | stop [name|all] | status | log [name] | open [name] | share [project] | edit"

        Private ReadOnly Property Runs As ProjectRuns
            Get
                If _projectRuns Is Nothing Then _projectRuns = New ProjectRuns(State.Shells)
                Return _projectRuns
            End Get
        End Property

        ''' <summary>The session's folder first, then the folders added with /add-dir.</summary>
        Private Function RunRoots() As List(Of String)
            Dim list As New List(Of String) From {State.ProjectDir}
            list.AddRange(State.ExtraDirs)
            Return list
        End Function

        Private Shared ReadOnly RunKindIcon As New Dictionary(Of String, String) From {{"web", "🌐"}, {"static", "🌐"}, {"app", "▶"}, {"test", "🧪"}}

        Private Async Function RunCommandAsync(arg As String) As Task
            Dim a = If(arg, "").Trim()
            Dim verb = a.Split(" "c)(0).ToLowerInvariant()
            Dim rest = If(a.Length > verb.Length, a.Substring(verb.Length).Trim(), "")
            Dim configs = ProjectRuns.Configs(RunRoots())
            Select Case verb
                Case "stop"
                    If rest = "" OrElse rest.Equals("all", StringComparison.OrdinalIgnoreCase) Then
                        Dim n = Runs.StopAll()
                        If n = 0 Then Out.Note("Nothing is running.") Else Out.Success($"Stopped {n}.")
                    Else
                        Dim c = FindRunConfig(configs, rest)
                        If c Is Nothing OrElse Not Runs.Stop(c.Id) Then Out.Note($"{rest} is not running.") Else Out.Success("Stopped " & c.Name)
                    End If
                Case "status", "list", "ls"
                    ShowRunList(configs)
                Case "log", "logs", "output"
                    Dim run = PickRun(configs, rest)
                    If run Is Nothing Then Return
                    Dim text = ProjectRuns.OutputFrom(run, 0).Text
                    Dim lines = text.Replace(vbCr, "").Split(ChrW(10)).Where(Function(l) l.Length > 0).ToList()
                    Out.Line(Ansi.Bold(run.Config.Name) & Ansi.Muted($"  {run.Config.Command}  ·  {RunState(run)}"))
                    For Each l In lines.Skip(Math.Max(0, lines.Count - 80))
                        Out.Line(Ansi.Muted("  │ ") & l)
                    Next
                Case "open"
                    Dim run = PickRun(configs, rest, webOnly:=True)
                    If run Is Nothing Then Return
                    If run.Url Is Nothing Then
                        Out.Note($"{run.Config.Name} has no address yet: /run log {run.Config.Id} shows its output.")
                    ElseIf OpenUrl(run.Url) Then
                        Out.Success("Opened " & run.Url)
                    Else
                        Out.Note("Open " & run.Url & " in your browser.")
                    End If
                Case "share"
                    Await ShareRunProjectAsync(rest).ConfigureAwait(False)
                Case "edit"
                    Dim file = ProjectRuns.EnsureLaunchFile(State.ProjectDir)
                    If OpenInEditor(file) Then Out.Success("Saved: /run uses " & Path.GetRelativePath(State.ProjectDir, file))
                Case "help", "?"
                    Out.Line(RunHelp)
                    Out.Line(Ansi.Muted("  How to run comes from .agentic/launch.json (or Claude Code's .claude/launch.json), else from the project's files."))
                Case ""
                    If configs.Count = 0 Then
                        Out.Note("No way to run this project was found. Ask Agentic to set it up (it writes .agentic/launch.json), or /run edit.")
                        Return
                    End If
                    Dim live = Runs.All().Where(Function(r) r.Running).ToDictionary(Function(r) r.Config.Id, Function(r) r, StringComparer.OrdinalIgnoreCase)
                    Dim multi = configs.Select(Function(c) c.Project).Distinct().Count() > 1
                    Dim options = configs.Select(Function(c)
                                                     Dim r As ProjectRun = Nothing
                                                     Dim state = If(live.TryGetValue(c.Id, r), "  · running" & If(r.Url IsNot Nothing, " at " & r.Url, ""), "")
                                                     Dim icon As String = Nothing
                                                     If Not RunKindIcon.TryGetValue(c.Kind, icon) Then icon = "▶"
                                                     Return New MenuOption($"{icon} {If(multi, c.ProjectName & " · ", "")}{c.Name}", If(c.Command, "built-in server") & state, c.Id)
                                                 End Function).ToList()
                    Dim i = Menu.Select(" Run this project", options, footer:="Enter runs it (or shows it when it runs) · Esc closes")
                    If i < 0 Then Return
                    Await StartRunAsync(configs(i)).ConfigureAwait(False)
                Case Else
                    Dim c = FindRunConfig(configs, a)
                    If c Is Nothing Then
                        Out.Fail($"No way to run ""{a}"". Known: {If(configs.Count = 0, "none", String.Join(", ", configs.Select(Function(x) x.Name)))}. {RunHelp}")
                        Return
                    End If
                    Await StartRunAsync(c).ConfigureAwait(False)
            End Select
        End Function

        Private Shared Function FindRunConfig(configs As List(Of RunConfig), q As String) As RunConfig
            Dim n As Integer
            If Integer.TryParse(q, n) AndAlso n >= 1 AndAlso n <= configs.Count Then Return configs(n - 1)
            Return If(configs.FirstOrDefault(Function(c) c.Id.Equals(q, StringComparison.OrdinalIgnoreCase) OrElse c.Name.Equals(q, StringComparison.OrdinalIgnoreCase)),
                      configs.FirstOrDefault(Function(c) c.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 OrElse If(c.Command, "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0))
        End Function

        ''' <summary>The run a command means: the named one, else the only (or the first) running one.</summary>
        Private Function PickRun(configs As List(Of RunConfig), q As String, Optional webOnly As Boolean = False) As ProjectRun
            Dim all = Runs.All()
            If Not String.IsNullOrWhiteSpace(q) Then
                Dim c = FindRunConfig(configs, q)
                Dim r = If(c Is Nothing, Nothing, Runs.Get(c.Id))
                If r Is Nothing Then Out.Note($"{q} has not run in this session.")
                Return r
            End If
            Dim pick = all.Where(Function(r) r.Running AndAlso (Not webOnly OrElse r.Url IsNot Nothing)).LastOrDefault()
            If pick Is Nothing Then pick = all.LastOrDefault()
            If pick Is Nothing Then Out.Note("Nothing has run in this session: /run starts something.")
            Return pick
        End Function

        Private Shared Function RunState(r As ProjectRun) As String
            If r.Running Then Return "running" & If(r.Url IsNot Nothing, " at " & r.Url, "")
            Return If(r.ExitCode.GetValueOrDefault() = 0, "ended", $"exited with code {r.ExitCode}")
        End Function

        Private Sub ShowRunList(configs As List(Of RunConfig))
            If configs.Count = 0 Then
                Out.Note("No way to run this project was found: /run edit writes .agentic/launch.json.")
                Return
            End If
            Dim i = 0
            For Each c In configs
                i += 1
                Dim r = Runs.Get(c.Id)
                Dim icon As String = Nothing
                If Not RunKindIcon.TryGetValue(c.Kind, icon) Then icon = "▶"
                Dim state = If(r Is Nothing, "", "  " & If(r.Running, Ansi.Ok(RunState(r)), If(r.ExitCode.GetValueOrDefault() = 0, Ansi.Muted(RunState(r)), Ansi.Bad(RunState(r)))))
                Out.Line($"  {i,2}. {icon} {Ansi.Bold(c.Name)}  {Ansi.Muted(If(c.Command, "built-in server"))}{state}")
            Next
            Out.Line(Ansi.Muted("  " & RunHelp))
        End Sub

        ''' <summary>
        ''' Starts a configuration (or shows it when it runs) and follows it: a web app until its address is known, the app or tests
        ''' until they end. Esc stops following (it keeps running: /run log, /run stop).
        ''' </summary>
        Private Async Function StartRunAsync(cfg As RunConfig) As Task
            Dim existing = Runs.Get(cfg.Id)
            If existing IsNot Nothing AndAlso existing.Running Then
                ProjectRuns.ScanForUrl(existing)
                Out.Note($"{cfg.Name} is {RunState(existing)}." & If(existing.Url IsNot Nothing, " /run open shows it in your browser.", " /run log shows its output."))
                Return
            End If
            Dim run As ProjectRun
            Try
                run = Runs.Start(cfg)
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is ComponentModel.Win32Exception OrElse TypeOf ex Is InvalidOperationException OrElse
                                       TypeOf ex Is Net.HttpListenerException
                Out.Fail($"{cfg.Name} could not start: {ex.Message}")
                Return
            End Try
            Out.Line(Ansi.Accent("▶ ") & Ansi.Bold(cfg.Name) & Ansi.Muted("  " & If(cfg.Command, "built-in server for " & cfg.Cwd) & "   (Esc stops following; it keeps running)"))
            Dim web = cfg.Kind = "web" OrElse cfg.Kind = "static"
            Dim pos = 0
            Dim deadline = DateTime.UtcNow.AddSeconds(If(web, 90, 3600))
            Dim followed = True
            Dim ended = False
            While DateTime.UtcNow < deadline
                Dim got = ProjectRuns.OutputFrom(run, pos)
                pos = got.Pos
                ' Whole lines as they come.
                For Each l In got.Text.Replace(vbCr, "").Split(ChrW(10))
                    If l.Length > 0 Then Out.Line(Ansi.Muted("  │ ") & l)
                Next
                ProjectRuns.ScanForUrl(run)
                If web AndAlso run.Url IsNot Nothing Then Exit While
                If Not run.Running Then
                    ' Its last lines may still be on their way.
                    Await Task.Delay(300).ConfigureAwait(False)
                    Dim tail = ProjectRuns.OutputFrom(run, pos)
                    For Each l In tail.Text.Replace(vbCr, "").Split(ChrW(10))
                        If l.Length > 0 Then Out.Line(Ansi.Muted("  │ ") & l)
                    Next
                    ended = True
                    Exit While
                End If
                If EscPressed() Then
                    followed = False
                    Exit While
                End If
                Await Task.Delay(150).ConfigureAwait(False)
            End While
            If ended Then
                If run.ExitCode.GetValueOrDefault() = 0 Then
                    Out.Success($"{cfg.Name}: done")
                Else
                    Out.Fail($"{cfg.Name} exited with code {run.ExitCode}. Ask Agentic to fix it, or /run log {cfg.Id}.")
                End If
            ElseIf web AndAlso run.Url IsNot Nothing Then
                Out.Success($"{cfg.Name} runs at {run.Url}")
                Out.Line(Ansi.Muted("  /run open shows it in your browser · /run stop stops it · /run share shares the website"))
            ElseIf Not followed Then
                Out.Note($"{cfg.Name} keeps running: /run log {cfg.Id} shows its output, /run stop {cfg.Id} stops it.")
            Else
                Out.Note($"{cfg.Name} is still starting: /run log {cfg.Id} shows its output.")
            End If
        End Function

        ''' <summary>Esc or Ctrl+C (other keys are dropped while a run is followed).</summary>
        Private Shared Function EscPressed() As Boolean
            Dim pressed = False
            While SafeKeyAvailable()
                Dim k = Console.ReadKey(True)
                If k.Key = ConsoleKey.Escape OrElse (k.Key = ConsoleKey.C AndAlso (k.Modifiers And ConsoleModifiers.Control) <> 0) Then pressed = True
            End While
            Return pressed
        End Function

        Private Shared Function OpenUrl(url As String) As Boolean
            Try
                If OperatingSystem.IsWindows() Then
                    Process.Start(New ProcessStartInfo(url) With {.UseShellExecute = True})?.Dispose()
                    Return True
                End If
                Dim opener = If(Platform.Which(If(OperatingSystem.IsMacOS(), "open", "xdg-open")), Nothing)
                If opener Is Nothing Then Return False
                Dim psi As New ProcessStartInfo(opener) With {.UseShellExecute = False}
                psi.ArgumentList.Add(url)
                Process.Start(psi)?.Dispose()
                Return True
            Catch ex As Exception When TypeOf ex Is ComponentModel.Win32Exception OrElse TypeOf ex Is InvalidOperationException
                Return False
            End Try
        End Function

        ''' <summary>/run share [project]: uploads the project's website (its build, or a static site's files) and prints its public link.</summary>
        Private Async Function ShareRunProjectAsync(q As String) As Task
            Dim projects = ProjectRuns.Projects(RunRoots())
            Dim p = If(String.IsNullOrWhiteSpace(q), If(projects.FirstOrDefault(Function(x) x.Share IsNot Nothing), projects.FirstOrDefault()),
                       projects.FirstOrDefault(Function(x) x.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0))
            If p Is Nothing Then
                Out.Fail(If(String.IsNullOrWhiteSpace(q), "No project to share.", $"No project ""{q}"". Projects: {String.Join(", ", projects.Select(Function(x) x.Name))}."))
                Return
            End If
            If p.Share Is Nothing Then
                Out.Fail($"{p.Name} has no website to share yet: it needs index.html, or a build (npm run build makes dist).")
                Return
            End If
            Dim zip As Byte()
            Try
                zip = ProjectRuns.ZipSite(Path.GetFullPath(Path.Combine(p.Folder, p.Share)), 25L * 1024 * 1024)
            Catch ex As Exception When TypeOf ex Is InvalidOperationException OrElse TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
                Out.Fail(ex.Message)
                Return
            End Try
            Out.Line(Ansi.Muted($"  Uploading {If(p.Share = ".", p.Name, p.Name & "/" & p.Share)} ({Math.Max(1, zip.Length \ 1024)} KB)…"))
            Try
                Dim r = Await _api.PostBytesAsync($"/api/chat/artifacts/share-site?title={Uri.EscapeDataString(p.Name)}&source={Uri.EscapeDataString(p.Name)}&key={Uri.EscapeDataString(p.Key)}",
                                                  zip, CancellationToken.None).ConfigureAwait(False)
                Dim link As Text.Json.JsonElement
                Dim url = If(r.TryGetProperty("link", link), Jx.StrOf(link, "url"), Nothing)
                Dim updated = Jx.BoolOf(r, "updated")
                Out.Success($"{If(updated, "Link updated", "Shared")}: {url}" & If(Clipboard.SetText(url), " (copied)", ""))
                Out.Line(Ansi.Muted("  Anyone with the link can open it. The Artifacts page in the web app lists your links and stops sharing."))
            Catch ex As ApiException
                Out.Fail("Sharing failed: " & ex.Message)
            End Try
        End Function

    End Class

End Namespace
