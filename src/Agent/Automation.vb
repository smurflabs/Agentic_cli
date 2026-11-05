Option Strict On
Option Explicit On
Option Infer On

Imports System.IO
Imports System.Text.RegularExpressions
Imports AgenticCli.Core
Imports AgenticCli.Tools
Imports AgenticCli.Ui

Namespace Agent

    ''' <summary>A prompt that runs again and again while the session waits for you (/loop).</summary>
    Friend NotInheritable Class LoopJob
        Public Property Id As Integer
        Public Property Every As TimeSpan
        Public Property Prompt As String
        Public Property NextUtc As DateTime
        Public Property Runs As Integer
    End Class

    ' Recurring work while the session is idle: /loop runs a prompt or command every few minutes (like Claude Code's /loop),
    ' and /heartbeat checks HEARTBEAT.md on a schedule (like OpenClaw's heartbeat). Both only start when the message box is
    ' empty and no answer is running; typing pauses them.
    Partial Friend NotInheritable Class Session

        Private ReadOnly _loops As New List(Of LoopJob)
        Private _loopSeq As Integer
        Private _heartbeatEvery As TimeSpan?
        Private _heartbeatNext As DateTime
        ''' <summary>What the prompt the idle timer just started is (shown instead of echoing it as typed).</summary>
        Private _idleNote As String

        Public Const HeartbeatOk As String = "HEARTBEAT_OK"

        ''' <summary>"30s", "5m", "2h", "1h30m", "90" (minutes): a duration; Nothing when it is not one.</summary>
        Friend Shared Function ParseInterval(text As String) As TimeSpan?
            Dim t = If(text, "").Trim().ToLowerInvariant()
            If Regex.IsMatch(t, "^\d+$") Then Return TimeSpan.FromMinutes(Integer.Parse(t))
            Dim m = Regex.Match(t, "^(?:(\d+)h)?(?:(\d+)m(?:in)?)?(?:(\d+)s)?$")
            If Not m.Success OrElse t.Length = 0 Then Return Nothing
            Dim part = Function(i As Integer) If(m.Groups(i).Success, Integer.Parse(m.Groups(i).Value), 0)
            Dim span = TimeSpan.FromHours(part(1)) + TimeSpan.FromMinutes(part(2)) + TimeSpan.FromSeconds(part(3))
            If span <= TimeSpan.Zero Then Return Nothing
            Return span
        End Function

        Friend Shared Function FormatInterval(t As TimeSpan) As String
            If t.TotalSeconds < 60 Then Return $"{CInt(t.TotalSeconds)}s"
            If t.TotalMinutes < 60 Then Return $"{CInt(t.TotalMinutes)}m"
            Dim h = CInt(Math.Floor(t.TotalHours))
            Dim mins = t.Minutes
            Return If(mins = 0, $"{h}h", $"{h}h{mins}m")
        End Function

        ''' <summary>The editor asks while it waits for a key: a loop or the heartbeat that is due takes over the empty box.</summary>
        Private Function DueIdlePrompt() As String
            Dim now = DateTime.UtcNow
            ' Monitor events first: what the watched commands printed (a burst of lines arrives together).
            If State.Monitors IsNot Nothing Then
                Dim ev = State.Monitors.TakeEvents(TimeSpan.FromSeconds(3))
                If ev.Prompt IsNot Nothing Then
                    _idleNote = "monitor " & ev.Note
                    Return ev.Prompt
                End If
            End If
            For Each job In _loops.OrderBy(Function(x) x.NextUtc).ToList()
                If job.NextUtc > now Then Continue For
                job.Runs += 1
                job.NextUtc = now + job.Every
                _idleNote = $"/loop #{job.Id} · every {FormatInterval(job.Every)} · run {job.Runs}: {ToolKit.OneLine(job.Prompt, 70)}"
                Return job.Prompt
            Next
            If _heartbeatEvery.HasValue AndAlso _heartbeatNext <= now Then
                _heartbeatNext = now + _heartbeatEvery.Value
                Dim prompt = HeartbeatPrompt()
                If prompt IsNot Nothing Then
                    _idleNote = $"heartbeat · every {FormatInterval(_heartbeatEvery.Value)} · {Paths.Display(HeartbeatFile(), State.Cwd)}"
                    Return prompt
                End If
            End If
            Return Nothing
        End Function

        Private Function HasIdleWork() As Boolean
            Return _loops.Count > 0 OrElse _heartbeatEvery.HasValue OrElse (State.Monitors IsNot Nothing AndAlso State.Monitors.Busy)
        End Function

        ''' <summary>/monitors [stop &lt;#&gt;|all]: the commands the Monitor tool watches.</summary>
        Private Sub MonitorsCommand(rest As String)
            Dim running = If(State.Monitors?.Running(), New List(Of MonitorJob))
            Dim m = Regex.Match(rest.Trim(), "^stops+(#?(d+)|all)$", RegexOptions.IgnoreCase)
            If m.Success Then
                Dim stopped = 0
                For Each j In running.Where(Function(x) m.Groups(1).Value.Equals("all", StringComparison.OrdinalIgnoreCase) OrElse x.Id.ToString() = m.Groups(2).Value)
                    If State.Monitors.Stop(j.Id) Then stopped += 1
                Next
                If stopped = 0 Then Out.Fail("No such monitor.") Else Out.Success($"Stopped {stopped} monitor(s)")
                Return
            End If
            If running.Count = 0 Then
                Out.Note("No monitors. The model starts one with the Monitor tool when it wants to watch a command's output (a log, a build, a server).")
                Return
            End If
            For Each j In running
                Out.Line($"  #{j.Id} " & Ansi.Bold(j.Description) & Ansi.Muted($"  {ToolKit.OneLine(j.Shell.CommandText, 60)} · {j.Delivered} line(s) so far · shell {j.Shell.Id}"))
            Next
            Out.Line(Ansi.Muted("  /monitors stop <#|all>"))
        End Sub

        ' ------------------------------------------------------------------ /loop

        Private Sub LoopCommand(rest As String)
            If rest.Length = 0 OrElse rest.Equals("list", StringComparison.OrdinalIgnoreCase) Then
                If _loops.Count = 0 Then
                    Out.Note("No loops. /loop 5m <prompt> runs a prompt (or a /command) every 5 minutes while Agentic waits for you.")
                    Return
                End If
                For Each j In _loops
                    Dim due = j.NextUtc - DateTime.UtcNow
                    Out.Line($"  {Ansi.Accent("#" & j.Id)}  every {FormatInterval(j.Every)}  {Ansi.Muted($"ran {j.Runs}× · next in {FormatInterval(If(due < TimeSpan.Zero, TimeSpan.Zero, due))}")}  {ToolKit.OneLine(j.Prompt, 70)}")
                Next
                Out.Note("/loop stop <#|all> ends loops.")
                Return
            End If
            Dim stopMatch = Regex.Match(rest, "^(stop|cancel|off|kill)\b\s*#?(\S*)$", RegexOptions.IgnoreCase)
            If stopMatch.Success Then
                Dim which = stopMatch.Groups(2).Value
                If which.Length = 0 OrElse which.Equals("all", StringComparison.OrdinalIgnoreCase) Then
                    Dim n = _loops.Count
                    _loops.Clear()
                    Out.Success(If(n = 0, "No loops were running.", $"Stopped {n} loop{If(n = 1, "", "s")}."))
                    Return
                End If
                Dim id As Integer
                If Integer.TryParse(which, id) AndAlso _loops.RemoveAll(Function(j) j.Id = id) > 0 Then Out.Success($"Stopped loop #{id}.") Else Out.Fail($"No loop #{which}. /loop lists them.")
                Return
            End If
            Dim words = rest.Split(" "c, 2)
            Dim every = ParseInterval(words(0))
            Dim prompt = If(every.HasValue, If(words.Length > 1, words(1).Trim(), ""), rest)
            If prompt.Length = 0 Then
                Out.Fail("Usage: /loop [interval] <prompt>   e.g. /loop 5m check whether the deploy finished   (interval: 30s, 5m, 2h; default 10m)")
                Return
            End If
            Dim interval = If(every, TimeSpan.FromMinutes(10))
            If interval < TimeSpan.FromSeconds(30) Then interval = TimeSpan.FromSeconds(30)
            _loopSeq += 1
            Dim job As New LoopJob With {.Id = _loopSeq, .Every = interval, .Prompt = prompt, .NextUtc = DateTime.UtcNow}
            _loops.Add(job)
            Out.Success($"Loop #{job.Id}: every {FormatInterval(interval)} while Agentic waits for you, starting now. /loop stop {job.Id} ends it.")
        End Sub

        ' ------------------------------------------------------------------ /heartbeat (HEARTBEAT.md)

        ''' <summary>The checklist the heartbeat reads: the project's HEARTBEAT.md (or .agentic\HEARTBEAT.md), else ~\.agentic\HEARTBEAT.md.</summary>
        Private Function HeartbeatFile() As String
            For Each candidate In {Path.Combine(State.ProjectDir, "HEARTBEAT.md"), Path.Combine(State.ProjectDir, ".agentic", "HEARTBEAT.md"), Path.Combine(Paths.AgenticHome(), "HEARTBEAT.md")}
                If File.Exists(candidate) Then Return candidate
            Next
            Return Path.Combine(State.ProjectDir, "HEARTBEAT.md")
        End Function

        ''' <summary>The heartbeat's message; Nothing when HEARTBEAT.md is missing or has nothing to check (only headings and comments).</summary>
        Private Function HeartbeatPrompt() As String
            Dim file = HeartbeatFile()
            Dim text = Paths.ReadText(file)
            If text Is Nothing Then Return Nothing
            Dim meaningful = Regex.Replace(text, "<!--[\s\S]*?-->", "").Split(ChrW(10)).
                Select(Function(l) l.Trim()).Where(Function(l) l.Length > 0 AndAlso Not l.StartsWith("#")).ToList()
            If meaningful.Count = 0 Then Return Nothing
            Return "Heartbeat check. Go through the checklist below (from HEARTBEAT.md) and act on anything that needs attention now, " &
                   "using your tools where useful. Keep it short. If nothing needs attention, reply with exactly " & HeartbeatOk & " and nothing else." & vbLf & vbLf &
                   $"<heartbeat file=""{Paths.Display(file, State.Cwd)}"" time=""{DateTime.Now:yyyy-MM-dd HH:mm}"">" & vbLf & text.Trim() & vbLf & "</heartbeat>"
        End Function

        Private Async Function HeartbeatCommandAsync(rest As String) As Task
            Dim words = rest.Split({" "c}, StringSplitOptions.RemoveEmptyEntries)
            Dim verb = If(words.Length > 0, words(0).ToLowerInvariant(), "")
            Dim file = HeartbeatFile()
            Select Case verb
                Case "", "status"
                    Out.Line(If(_heartbeatEvery.HasValue, $"  Heartbeat {Ansi.Accent("on")}: every {FormatInterval(_heartbeatEvery.Value)}", $"  Heartbeat {Ansi.Muted("off")}"))
                    Out.Line($"  Checklist: {Paths.Display(file, State.Cwd)}{If(IO.File.Exists(file), "", Ansi.Warn(" (missing: create it with what to check)"))}")
                    Out.Note("/heartbeat on [interval] · off · now. When nothing needs attention the answer is just " & HeartbeatOk & ".")
                Case "on", "start", "every"
                    Dim interval = If(words.Length > 1, ParseInterval(words(1)), Nothing)
                    _heartbeatEvery = If(interval.HasValue AndAlso interval.Value >= TimeSpan.FromMinutes(1), interval.Value, TimeSpan.FromMinutes(30))
                    _heartbeatNext = DateTime.UtcNow + _heartbeatEvery.Value
                    If Not IO.File.Exists(file) Then
                        IO.File.WriteAllText(file, "# Heartbeat" & vbLf & vbLf & "<!-- What Agentic checks every time the heartbeat runs. Lines starting with # and comments are ignored. -->" & vbLf &
                                             "- Are the tests still passing?" & vbLf)
                        Out.Note($"Created {Paths.Display(file, State.Cwd)}: edit it to say what to check.")
                    End If
                    Out.Success($"Heartbeat on: every {FormatInterval(_heartbeatEvery.Value)} while Agentic waits for you (first check in {FormatInterval(_heartbeatEvery.Value)}; /heartbeat now runs one at once).")
                Case "off", "stop"
                    _heartbeatEvery = Nothing
                    Out.Success("Heartbeat off.")
                Case "now", "run"
                    Dim prompt = HeartbeatPrompt()
                    If prompt Is Nothing Then
                        Out.Fail($"{Paths.Display(file, State.Cwd)} is missing or has nothing to check.")
                        Return
                    End If
                    Out.Line(Ansi.Info("♥ heartbeat") & Ansi.Muted(" · " & Paths.Display(file, State.Cwd)))
                    Await SendAsync(prompt).ConfigureAwait(False)
                Case Else
                    Out.Fail("Usage: /heartbeat [on [interval]|off|now|status]")
            End Select
        End Function

    End Class

End Namespace
