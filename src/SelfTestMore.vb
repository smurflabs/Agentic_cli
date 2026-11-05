Option Strict On
Option Explicit On
Option Infer On

Imports System.IO
Imports System.Text.RegularExpressions
Imports AgenticCli.Agent
Imports AgenticCli.Core
Imports AgenticCli.Tools
Imports AgenticCli.Ui

''' <summary>
''' Self-tests of Ctrl+G (the message in an editor), Alt+V (an image from the clipboard), desktop notifications, git
''' worktrees (--worktree, /worktree) and --image.
''' </summary>
Partial Friend NotInheritable Class SelfTest

    ''' <summary>The plan gauge on the working line fills as steps complete and goes away when all are done.</summary>
    Private Shared Function TodoList(ParamArray states As String()) As List(Of TodoItem)
        Return states.Select(Function(s, i) New TodoItem With {.Content = "step " & i, .Status = s}).ToList()
    End Function

    Private Shared Sub PlanGaugeTests()
        Dim items As Func(Of String(), List(Of TodoItem)) = Function(s) TodoList(s)
        Dim plain = Function(s As String) Regex.Replace(s, "\x1b\[[0-9;]*m", "")
        Check("plan gauge: none without a list", TurnRunner.PlanGauge(New List(Of TodoItem)) = "")
        Check("plan gauge: empty at the start", plain(TurnRunner.PlanGauge(items({"in_progress", "pending", "pending", "pending"}))) = "▱▱▱▱▱ 0/4 · ")
        Check("plan gauge: fills as steps complete", plain(TurnRunner.PlanGauge(items({"completed", "completed", "in_progress", "pending"}))) = "▰▰▱▱▱ 2/4 · ",
              plain(TurnRunner.PlanGauge(items({"completed", "completed", "in_progress", "pending"}))))
        Check("plan gauge: one step done shows", plain(TurnRunner.PlanGauge(items({"completed", "pending", "pending", "pending", "pending", "pending", "pending"}))).StartsWith("▰▱▱▱▱ 1/7"))
        Check("plan gauge: gone when all are done", TurnRunner.PlanGauge(items({"completed", "completed"})) = "")
    End Sub

    ''' <summary>The Monitor tool: matching output lines become events, the exit too; the time limit stops a command.</summary>
    Private Shared Sub MonitorTests(tmp As String)
        Using monitors As New MonitorManager(New ShellManager("auto"))
            ' "echo" works in PowerShell and in bash.
            monitors.Start("echo alpha; echo 'ERROR beta'; echo gamma", tmp, "build", New Regex("ERROR"), TimeSpan.FromMinutes(1))
            Dim got As String = Nothing
            For i = 1 To 100
                Dim ev = monitors.TakeEvents(TimeSpan.Zero)
                If ev.Prompt IsNot Nothing Then got &= ev.Prompt & vbLf
                If got IsNot Nothing AndAlso got.Contains("exited with code") Then Exit For
                Threading.Thread.Sleep(100)
            Next
            got = If(got, "")
            Check("monitor: a matching line arrives as an event", got.Contains("[monitor #1 build] ERROR beta"), got)
            Check("monitor: lines that do not match are left out", Not got.Contains("alpha") AndAlso Not got.Contains("gamma"), got)
            Check("monitor: the exit arrives too", got.Contains("exited with code 0"), got)
            Check("monitor: done means idle", Not monitors.Busy)
            ' The time limit stops a command that keeps running.
            Dim slow = If(OperatingSystem.IsWindows(), "Start-Sleep -Seconds 30", "sleep 30")
            If OperatingSystem.IsWindows() AndAlso New ShellManager("auto").Kind = "cmd" Then slow = "ping -n 30 127.0.0.1"
            monitors.Start(slow, tmp, "slow", Nothing, TimeSpan.FromMilliseconds(800))
            got = ""
            For i = 1 To 100
                Dim ev = monitors.TakeEvents(TimeSpan.Zero)
                If ev.Prompt IsNot Nothing Then got &= ev.Prompt
                If got.Contains("time limit") Then Exit For
                Threading.Thread.Sleep(100)
            Next
            Check("monitor: the time limit stops it", got.Contains("[monitor #2 slow] stopped: its time limit ran out"), got)
        End Using
        ' Bash rules cover Monitor: it runs a command the same way.
        Dim state = NewState(tmp)
        Dim p As New PermissionEngine(state)
        Dim mon As New MonitorTool()
        Check("monitor: asks like a command", p.Evaluate(mon, Jx.Parse(J(New With {.command = "npm run dev", .description = "dev"}))).Verdict = Verdict.Ask)
        p.SessionAllow.Add(PermissionRule.Parse("Bash(npm run dev:*)", "test"))
        Check("monitor: a Bash rule allows it", p.Evaluate(mon, Jx.Parse(J(New With {.command = "npm run dev", .description = "dev"}))).Verdict = Verdict.Allow)
    End Sub

    Private Shared Sub MoreFeatureTests(tmp As String)
        ' Ctrl+G: the editor gets the message, and what it saves replaces it.
        Dim t As New FakeTerminal()
        t.Type("draft")
        t.Press(ConsoleKey.G, ctrl:=True, ch:=ChrW(7))
        t.Type(vbLf)
        Dim seen As String = Nothing
        Dim ed As New LineEditor(t) With {.ExternalEdit = Function(s)
                                                              seen = s
                                                              Return "edited in the editor" & vbCrLf
                                                          End Function}
        Dim text = ed.ReadLine()
        Check("keys: Ctrl+G edits the message in an editor", seen = "draft" AndAlso text = "edited in the editor", $"{seen} / {text}")

        ' Alt+V: the clipboard's image goes in as a placeholder; without one, the message stays as it was.
        t = New FakeTerminal()
        t.Type("look at ")
        t.Keys.Enqueue(New ConsoleKeyInfo("v"c, ConsoleKey.V, False, True, False))
        t.Type(vbLf)
        text = New LineEditor(t) With {.PasteImage = Function() "[Image #1]"}.ReadLine()
        Check("keys: Alt+V pastes a clipboard image as [Image #1]", If(text, "").Trim() = "look at [Image #1]", text)
        t = New FakeTerminal()
        t.Type("plain")
        t.Keys.Enqueue(New ConsoleKeyInfo("v"c, ConsoleKey.V, False, True, False))
        t.Type(vbLf)
        text = New LineEditor(t) With {.PasteImage = Function() Nothing}.ReadLine()
        Check("keys: Alt+V without an image changes nothing", text = "plain", text)
        Check("keys: Ctrl+G and Alt+V are free in the message box",
              KeyBindings.Conflicts("external-editor", "Ctrl+G").Count = 0 AndAlso KeyBindings.Conflicts("paste-image", "Alt+V").Count = 0)

        ' Notifications: the text is escaped for the toast's XML (and the PowerShell string around it).
        Dim script = DesktopNotify.ToastScript("A <b> & 'c'", "done")
        Check("notify: the toast's text is escaped", script.Contains("A &lt;b&gt; &amp; &apos;c&apos;") AndAlso Not script.Contains("<b>"), script)

        ' --worktree and --image on the command line.
        Dim o = CliOptions.Parse({"-w", "feature-x", "fix it"})
        Check("options: --worktree takes a name", o.Worktree = "feature-x" AndAlso o.Prompt = "fix it", $"{o.Worktree}|{o.Prompt}")
        o = CliOptions.Parse({"--worktree", "fix the bug"})
        Check("options: --worktree without a name", o.Worktree = "" AndAlso o.Prompt = "fix the bug", $"{o.Worktree}|{o.Prompt}")
        o = CliOptions.Parse({"-i", "a.png", "b.png", "-p", "describe"})
        Check("options: --image takes several files", o.Images.SequenceEqual({"a.png", "b.png"}) AndAlso o.Print AndAlso o.Prompt = "describe",
              String.Join(",", o.Images) & "|" & o.Prompt)
        Check("worktree: names git accepts", Worktrees.NameFrom("fix login bug!") = "fix-login-bug" AndAlso Worktrees.NameFrom("").StartsWith("session-"))

        ' A real worktree in a scratch repository (when git is installed).
        If Platform.Which("git") Is Nothing Then Return
        Dim repo = Path.Combine(tmp, "wt-repo")
        Directory.CreateDirectory(repo)
        Try
            ProjectInfo.RunQuiet("git", "init -q", repo, 20000)
            ProjectInfo.RunQuiet("git", "-c user.email=selftest@example.com -c user.name=selftest commit --allow-empty -q -m init", repo, 20000)
            Dim wt = Worktrees.Create(repo, "feature one")
            Dim folder = Path.Combine(repo, ".agentic", "worktrees", "feature-one")
            Check("worktree: made in .agentic\worktrees on its own branch", wt.Error Is Nothing AndAlso Directory.Exists(folder) AndAlso wt.Branch = "agentic/feature-one",
                  If(wt.Error, wt.Folder))
            Dim exclude = Path.Combine(repo, ".git", "info", "exclude")
            Check("worktree: git ignores the worktrees folder", File.Exists(exclude) AndAlso File.ReadAllText(exclude).Contains("/.agentic/worktrees/"))
            Check("worktree: listed", If(Worktrees.List(repo), "").Contains("feature-one"))
            Check("worktree: the main checkout is found from inside it", String.Equals(Path.GetFullPath(If(Worktrees.MainRoot(folder), "")).TrimEnd("\"c, "/"c),
                                                                                     Path.GetFullPath(repo).TrimEnd("\"c, "/"c), StringComparison.OrdinalIgnoreCase), Worktrees.MainRoot(folder))
            Dim again = Worktrees.Create(repo, "feature one")
            Check("worktree: the same name is used again", again.Error Is Nothing AndAlso again.Folder = wt.Folder)
            Check("worktree: removed", Worktrees.Remove(repo, "feature-one", False) Is Nothing AndAlso Not Directory.Exists(folder))
        Finally
            ' git makes its object files read-only: make them writable so the scratch folder can be deleted.
            Try
                For Each f In Directory.EnumerateFiles(repo, "*", SearchOption.AllDirectories)
                    File.SetAttributes(f, FileAttributes.Normal)
                Next
                Directory.Delete(repo, True)
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
            End Try
        End Try
    End Sub

End Class
