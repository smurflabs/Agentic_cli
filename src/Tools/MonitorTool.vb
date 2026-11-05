Option Strict On
Option Explicit On
Option Infer On

Imports System.Text
Imports System.Text.Json
Imports System.Text.Json.Nodes
Imports System.Text.RegularExpressions
Imports AgenticCli.Agent
Imports AgenticCli.Ui

Namespace Tools

    ''' <summary>A watched command (the Monitor tool): its output lines that match the filter arrive in the conversation.</summary>
    Friend NotInheritable Class MonitorJob
        Public Property Id As Integer
        Public Property Description As String
        Public Property Shell As BackgroundShell
        Public Property Filter As Regex
        Public Property Deadline As DateTime
        ''' <summary>How far the output has been read, and the start of a line not finished yet.</summary>
        Public Property ReadTo As Integer
        Public Property Pending As String = ""
        Public Property Ended As Boolean
        Public Property Delivered As Integer
        Public Property Dropped As Integer
    End Class

    ''' <summary>
    ''' The session's monitors: each runs a command in the background and turns its output lines (those matching its filter) into
    ''' events. The session hands pending events to the model as a message while it waits for the user (like /loop), so the
    ''' model can react to a log, a build or a server as things happen.
    ''' </summary>
    Friend NotInheritable Class MonitorManager
        Implements IDisposable

        Public Const MaxPending As Integer = 400
        Private ReadOnly _shells As ShellManager
        Private ReadOnly _jobs As New List(Of MonitorJob)
        Private ReadOnly _events As New List(Of (Job As MonitorJob, Text As String))
        Private ReadOnly _timer As Threading.Timer
        Private _seq As Integer
        Private _lastDelivery As DateTime = DateTime.MinValue

        Public Sub New(shells As ShellManager)
            _shells = shells
            _timer = New Threading.Timer(Sub(s) Poll(), Nothing, 500, 500)
        End Sub

        Public Function Start(command As String, cwd As String, description As String, filter As Regex, timeout As TimeSpan) As MonitorJob
            Dim sh = _shells.StartBackground(command, cwd)
            Dim job As New MonitorJob With {.Shell = sh, .Description = description, .Filter = filter, .Deadline = DateTime.UtcNow + timeout}
            SyncLock _jobs
                _seq += 1
                job.Id = _seq
                _jobs.Add(job)
            End SyncLock
            Return job
        End Function

        Public Function Running() As List(Of MonitorJob)
            SyncLock _jobs
                Return _jobs.Where(Function(j) Not j.Ended).ToList()
            End SyncLock
        End Function

        Public Function [Stop](id As Integer) As Boolean
            Dim job As MonitorJob
            SyncLock _jobs
                job = _jobs.FirstOrDefault(Function(j) j.Id = id AndAlso Not j.Ended)
            End SyncLock
            If job Is Nothing Then Return False
            _shells.Kill(job.Shell.Id)
            Return True
        End Function

        ''' <summary>Monitors still running, or events not handed over yet.</summary>
        Public ReadOnly Property Busy As Boolean
            Get
                SyncLock _jobs
                    Return _events.Count > 0 OrElse _jobs.Any(Function(j) Not j.Ended)
                End SyncLock
            End Get
        End Property

        ''' <summary>
        ''' The pending events as one message for the model (Nothing when there are none, or the last were handed over less than
        ''' minGap ago, so a burst of lines arrives together), and a short note for the screen.
        ''' </summary>
        Public Function TakeEvents(minGap As TimeSpan) As (Prompt As String, Note As String)
            Poll()
            SyncLock _jobs
                If _events.Count = 0 OrElse DateTime.UtcNow - _lastDelivery < minGap Then Return (Nothing, Nothing)
                _lastDelivery = DateTime.UtcNow
                Dim sb As New StringBuilder("(Monitor events: output of commands you started with the Monitor tool, as it happened. React when something needs it " &
                                            "- an error, a finished build, a server that is ready - otherwise acknowledge it in a few words.)" & vbLf)
                For Each e In _events
                    sb.Append($"[monitor #{e.Job.Id} {e.Job.Description}] ").Append(e.Text).Append(vbLf)
                Next
                For Each j In _events.Select(Function(e) e.Job).Distinct().Where(Function(x) x.Dropped > 0)
                    sb.Append($"[monitor #{j.Id}] {j.Dropped} more line(s) were left out.").Append(vbLf)
                    j.Dropped = 0
                Next
                Dim jobs = _events.Select(Function(e) e.Job).Distinct().ToList()
                Dim note = String.Join(", ", jobs.Select(Function(j) $"#{j.Id} {ToolKit.OneLine(j.Description, 30)}")) & $" · {_events.Count} event(s)"
                _events.Clear()
                Return (sb.ToString().TrimEnd(), note)
            End SyncLock
        End Function

        ''' <summary>Reads new output of every monitor: complete lines that match become events; an exit or the deadline ends it.</summary>
        Private Sub Poll()
            Dim jobs As List(Of MonitorJob)
            SyncLock _jobs
                jobs = _jobs.Where(Function(j) Not j.Ended).ToList()
            End SyncLock
            For Each job In jobs
                Dim fresh As String
                SyncLock job.Shell.Output
                    Dim pos = Math.Min(job.ReadTo, job.Shell.Output.Length)
                    fresh = job.Shell.Output.ToString(pos, job.Shell.Output.Length - pos)
                    job.ReadTo = job.Shell.Output.Length
                End SyncLock
                Dim text = job.Pending & fresh.Replace(vbCrLf, vbLf).Replace(vbCr, vbLf)
                Dim lines = text.Split(ChrW(10))
                job.Pending = lines(lines.Length - 1)
                Dim ended = Not job.Shell.Running
                Dim expired = Not ended AndAlso DateTime.UtcNow > job.Deadline
                If ended OrElse expired Then
                    ' The last line without a line break counts too once the command is done.
                    If job.Pending.Length > 0 Then lines = lines.Take(lines.Length - 1).Concat({job.Pending}).Append("").ToArray()
                    job.Pending = ""
                End If
                SyncLock _jobs
                    For i = 0 To lines.Length - 2
                        Dim line = lines(i).TrimEnd()
                        If line.Length = 0 OrElse (job.Filter IsNot Nothing AndAlso Not job.Filter.IsMatch(line)) Then Continue For
                        If _events.Count >= MaxPending Then
                            job.Dropped += 1
                        Else
                            _events.Add((job, ToolKit.OneLine(line, 400)))
                            job.Delivered += 1
                        End If
                    Next
                    If expired Then
                        _shells.Kill(job.Shell.Id)
                        _events.Add((job, "stopped: its time limit ran out"))
                        job.Ended = True
                    ElseIf ended Then
                        _events.Add((job, If(job.Shell.Killed, "stopped", $"the command exited with code {If(job.Shell.ExitCode, -1)}")))
                        job.Ended = True
                    End If
                End SyncLock
            Next
        End Sub

        Public Sub Dispose() Implements IDisposable.Dispose
            _timer.Dispose()
        End Sub
    End Class

    ''' <summary>
    ''' Monitor: runs a command in the background and streams its output lines (those matching filter) into the conversation as
    ''' they happen (Claude Code's Monitor). Interactive sessions only: the lines arrive while the session waits for the user.
    ''' </summary>
    Friend NotInheritable Class MonitorTool
        Implements ITool

        Public ReadOnly Property Name As String = "Monitor" Implements ITool.Name
        Public ReadOnly Property IsReadOnly As Boolean = False Implements ITool.IsReadOnly
        Public ReadOnly Property Description As String =
            "Starts a command in the background and streams its output into this conversation as it happens: each new output line (only those matching " &
            "filter, a regular expression, when given) arrives as an event message while you wait, so you can react to a log, a build, tests or a server " &
            "without polling. Use it for anything long-running you want to watch (tail -f a log, a dev server, a watch build). The monitor ends when the " &
            "command exits or after timeout_ms (default 10 minutes, at most 1 hour); stop it earlier with KillShell and its shell id. Keep filter selective: " &
            "every matching line becomes a message." Implements ITool.Description

        Public Function Parameters() As JsonObject Implements ITool.Parameters
            Return ToolKit.Schema({"command", "description"}, ("command", "string", "The command to run and watch"),
                                  ("description", "string", "What is being watched, in a few words (shown with every event)"),
                                  ("filter", "string", "Optional regular expression: only matching lines become events (e.g. ERROR|Listening on|FAILED)"),
                                  ("timeout_ms", "number", "How long to watch at most (default 600000, at most 3600000)"))
        End Function

        Public Function Title(input As JsonElement, state As AgentState) As String Implements ITool.Title
            Return "Monitor(" & ToolKit.OneLine(ToolKit.Arg(input, "description", ToolKit.Arg(input, "command", "")), 70) & ")"
        End Function

        Public Function Preview(input As JsonElement, state As AgentState) As String Implements ITool.Preview
            Return "      " & Ansi.Muted(ToolKit.OneLine(ToolKit.Arg(input, "command", ""), 300)) & Environment.NewLine
        End Function

        Public Function RunAsync(input As JsonElement, ctx As ToolContext) As Task(Of ToolResult) Implements ITool.RunAsync
            Dim monitors = ctx.State.Monitors
            If monitors Is Nothing OrElse Not ctx.State.Interactive Then Return Task.FromResult(ToolResult.Fail("Monitor works in interactive sessions only: use Bash here."))
            If ctx.Depth > 0 Then Return Task.FromResult(ToolResult.Fail("Subagents cannot watch commands: the events go to the main conversation."))
            Dim command = ToolKit.Arg(input, "command")
            If String.IsNullOrWhiteSpace(command) Then Return Task.FromResult(ToolResult.Fail("command is required."))
            Dim description = ToolKit.OneLine(ToolKit.Arg(input, "description", command), 60)
            Dim filter As Regex = Nothing
            Dim pattern = ToolKit.Arg(input, "filter")
            If Not String.IsNullOrWhiteSpace(pattern) Then
                Try
                    filter = New Regex(pattern, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200))
                Catch ex As ArgumentException
                    Return Task.FromResult(ToolResult.Fail("filter is not a valid regular expression: " & ex.Message))
                End Try
            End If
            Dim ms = 600000
            Dim t = ToolKit.Arg(input, "timeout_ms")
            Dim parsed As Double
            If Not String.IsNullOrWhiteSpace(t) AndAlso Double.TryParse(t, Globalization.NumberStyles.Float, Globalization.CultureInfo.InvariantCulture, parsed) Then ms = CInt(Math.Clamp(parsed, 5000, 3600000))
            Dim job = monitors.Start(command, ctx.State.Cwd, description, filter, TimeSpan.FromMilliseconds(ms))
            Return Task.FromResult(ToolResult.Done($"Monitor #{job.Id} started (shell {job.Shell.Id}): watching ""{description}""{If(filter Is Nothing, "", $" for lines matching /{pattern}/")} " &
                                                   $"for up to {ms \ 60000} min. Its output lines arrive as event messages while you wait; stop it with KillShell {job.Shell.Id}.",
                                                   $"Watching {description} (#{job.Id})"))
        End Function
    End Class

End Namespace
