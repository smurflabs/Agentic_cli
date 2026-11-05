Option Strict On
Option Explicit On
Option Infer On

Imports System.Collections.Concurrent
Imports System.IO
Imports System.Net.Http
Imports System.Text
Imports System.Text.Json
Imports System.Text.Json.Nodes
Imports System.Threading
Imports AgenticCli.Api
Imports AgenticCli.Core
Imports AgenticCli.Tools
Imports AgenticCli.Ui

Namespace Agent

    Friend NotInheritable Class TurnOptions
        Public Property ChatId As String
        Public Property Content As String
        Public Property Attachments As New List(Of String)
        Public Property Regenerate As Boolean
        Public Property Model As String
        Public Property Server As ServerSettings
        Public Property Tools As New List(Of ITool)
        Public Property Instructions As String
        ''' <summary>How often the server checks the answer's progress (tool rounds); a hard stop only with MaxToolRoundsHard.</summary>
        Public Property MaxToolRounds As Integer
        ''' <summary>MaxToolRounds is a stop the user chose (--max-turns), not only a progress check.</summary>
        Public Property MaxToolRoundsHard As Boolean
        ''' <summary>0 = the main conversation, 1 = a subagent (shown compactly, under its Task line).</summary>
        Public Property Depth As Integer
        ''' <summary>A subagent's short name: its steps are tagged with it (several subagents may work at once).</summary>
        Public Property Label As String
        ''' <summary>Stream the answer to the terminal (off in print mode's JSON output and for subagents).</summary>
        Public Property Render As Boolean = True
        ''' <summary>Print mode: events as they happen (text, tool_use, tool_result).</summary>
        Public Property Observer As Action(Of String, Object)
        Public Property StopHookActive As Boolean
        ''' <summary>How often this question was asked again by itself because its answer was lost (a server restart).</summary>
        Public Property LostRetries As Integer
        ''' <summary>Follow an answer written for another window (the web app) in this chat from the live stream, instead of sending a message.</summary>
        Public Property Follow As Boolean
        ''' <summary>Following: only run the tool calls waiting for this CLI (no answer to show in the open chat).</summary>
        Public Property ToolsOnly As Boolean
        ''' <summary>/research: the server researches this message in depth (several searches, sources cross-checked and cited).</summary>
        Public Property Research As Boolean
    End Class

    Friend NotInheritable Class TurnResult
        Public Property Text As New StringBuilder()
        Public Property MessageId As String
        Public Property UserMessageId As String
        Public Property StopReason As String = "end"
        Public Property GoalStatus As String
        Public Property GoalSummary As String
        Public Property [Error] As String
        Public Property Cancelled As Boolean
        Public Property PromptTokens As Integer
        Public Property CompletionTokens As Integer
        ''' <summary>What the answer cost the account (micro-dollars; 0 when free or not charged).</summary>
        Public Property CostMicros As Long
        Public Property Context As ContextInfo
        Public Property Questions As New List(Of JsonElement)
        Public Property Title As String
        Public Property ToolUses As Integer
        Public Property FollowUp As String
        Public Property FollowUpFromStopHook As Boolean
        Public Property DurationMs As Long
        Public Property Rounds As Integer
        Public Property RoutedModel As String
        ''' <summary>Messages added while the answer ran that it did not get to (they go next).</summary>
        Public Property SteersLeft As New List(Of String)
        ''' <summary>A followed answer: the message another window sent.</summary>
        Public Property Prompt As String
        ''' <summary>A followed answer that no window sent: the server continues the chat's goal by itself.</summary>
        Public Property AutoGoal As Boolean
        ''' <summary>What the model thought, one entry per thinking phase (/thoughts shows them).</summary>
        Public Property Thoughts As New List(Of String)
        ''' <summary>Average generation speed of the answer (tokens per second while it was writing or thinking).</summary>
        Public Property TokensPerSecond As Double
    End Class

    ''' <summary>
    ''' One message and its answer: streams the server's events, renders text and tool calls like Claude Code, asks for
    ''' permission where needed, runs the tools the model calls on this computer and sends their results back.
    ''' </summary>
    Friend NotInheritable Class TurnRunner

        Private ReadOnly _chat As ChatApi
        Private ReadOnly _state As AgentState
        Private ReadOnly _perm As PermissionEngine
        Public Property ClientId As String = "cli-" & Guid.NewGuid().ToString("N").Substring(0, 10)
        Public Property RunSubagent As Func(Of String, String, String, ToolContext, Task(Of ToolResult))
        Public Property ApprovePlan As Func(Of String, (Choice As String, Feedback As String))
        ''' <summary>Stops the running answer (Esc in a permission question).</summary>
        Public Property Interrupt As Action
        ''' <summary>Streamed characters of the main answer (for the spinner's token count).</summary>
        Public Property StreamedChars As Integer
        ''' <summary>The live stream (web app sync): tool calls from other windows, approvals answered elsewhere.</summary>
        Public Property Link As LiveLink
        ''' <summary>
        ''' Parallel agents: subagents (Task) and read-only tools of one round run at the same time. The server sends a round's
        ''' parallel calls together and a call that changes something only after the earlier ones answered, so those still run alone.
        ''' </summary>
        Public Property Parallel As Boolean = True
        Public Property MaxParallelAgents As Integer = 6
        Public Property MaxParallelReads As Integer = 8
        Private _agentGate As SemaphoreSlim
        Private _readGate As SemaphoreSlim
        ''' <summary>Questions on the console are asked one at a time, even while tools run in parallel.</summary>
        Private ReadOnly _askLock As New Object()
        ''' <summary>Tools and subagents running in parallel right now (label per run), for the spinner.</summary>
        Private ReadOnly _running As New ConcurrentDictionary(Of String, String)(StringComparer.Ordinal)

        Private Shared ReadOnly Words As String() = {"Thinking", "Pondering", "Musing", "Cogitating", "Ruminating", "Mulling", "Deliberating", "Brewing", "Percolating",
            "Noodling", "Conjuring", "Synthesizing", "Contemplating", "Crafting", "Computing", "Wrangling", "Untangling", "Assembling", "Sketching", "Tinkering",
            "Distilling", "Weaving", "Hatching", "Simmering", "Marinating", "Churning", "Puzzling", "Reasoning", "Considering", "Figuring", "Envisioning", "Forging"}
        Private Shared ReadOnly Rng As New Random()
        ''' <summary>Recent (time, streamed characters) samples, for the tokens-per-second figure while it streams.</summary>
        Private ReadOnly _rate As New Queue(Of (At As DateTime, Chars As Integer))
        Private Shared _thoughtHintShown As Boolean

        Public Shared Function WorkingWord() As String
            Return Words(Rng.Next(Words.Length)) & "…"
        End Function

        Public Sub New(chat As ChatApi, state As AgentState, perm As PermissionEngine)
            _chat = chat
            _state = state
            _perm = perm
            _subView = New SubagentView(state)
        End Sub

        ''' <summary>Subagents at work: their live panel and what they write (the CLI's own and the server's).</summary>
        Private ReadOnly _subView As SubagentView

        Public Shared Function FormatTokens(n As Long) As String
            If n >= 1_000_000 Then Return (n / 1_000_000.0).ToString("0.#") & "M"
            If n >= 1000 Then Return (n / 1000.0).ToString("0.#") & "k"
            Return n.ToString()
        End Function

        Private Function BuildBody(o As TurnOptions) As String
            Dim tools As New JsonArray()
            For Each t In o.Tools
                tools.Add(New JsonObject From {{"name", t.Name}, {"description", t.Description}, {"parameters", t.Parameters()},
                                               {"x-risk", If(t.IsReadOnly, "read", "change")}})
            Next
            Dim body As New JsonObject From {
                {"content", If(o.Content, "")}, {"clientId", ClientId}, {"clientTools", tools}, {"clientInstructions", If(o.Instructions, "")}}
            ' This CLI as a device: the chat remembers it, so messages sent from the web app use its tools while it is connected.
            If Link IsNot Nothing AndAlso o.Depth = 0 Then body("device") = Link.DeviceId
            If Not String.IsNullOrWhiteSpace(o.Model) Then body("model") = o.Model
            If o.Server IsNot Nothing Then body("settings") = JsonNode.Parse(o.Server.ToJson())
            If o.Regenerate Then body("regenerate") = True
            If o.Research Then body("research") = True
            If o.MaxToolRounds > 0 Then body("maxToolRounds") = o.MaxToolRounds
            If o.MaxToolRounds > 0 AndAlso o.MaxToolRoundsHard Then body("maxToolRoundsHard") = True
            If o.Attachments IsNot Nothing AndAlso o.Attachments.Count > 0 Then body("attachments") = New JsonArray(o.Attachments.Select(Function(a) CType(JsonValue.Create(a), JsonNode)).ToArray())
            Return body.ToJsonString()
        End Function

        Public Async Function RunAsync(o As TurnOptions, ct As CancellationToken) As Task(Of TurnResult)
            Dim result As New TurnResult()
            Dim sw = Stopwatch.StartNew()
            Dim show = o.Render AndAlso o.Depth = 0
            ' A subagent shows in the panel above the spinner from its start ("working…" until it writes).
            If o.Depth > 0 AndAlso o.Render Then _subView.Start(o.ChatId, o.Label)
            Dim toolMap As New Dictionary(Of String, ITool)(StringComparer.OrdinalIgnoreCase)
            For Each t In o.Tools
                toolMap(t.Name) = t
            Next
            Dim md As MarkdownStream = If(show, New MarkdownStream(Sub(t) Out.Write(t)), Nothing)
            Dim thinking As MarkdownStream = If(show, New MarkdownStream(Sub(t) Out.Write(t), Ansi.Muted(Ansi.Therefore & " ")) With {.Muted = True}, Nothing)
            Dim approved As New HashSet(Of String)(StringComparer.Ordinal)
            Dim retryLost = False
            Dim thinkingSince As DateTime? = Nothing
            Dim chars = 0
            If o.Depth = 0 Then StreamedChars = 0
            Dim word = WorkingWord()
            If show Then Out.StartSpinner(word, Function() SpinnerMeta())

            ' Thinking shows live above the spinner while the model thinks; when it writes or uses a tool the thought folds
            ' into one line ("∴ Thought for 4s · 120 words", read in full with /thoughts). Each new thinking phase starts a new
            ' one, like the Chat tab's thinking blocks. Verbose mode (Ctrl+O) prints the whole thought instead.
            Dim thought As New StringBuilder()
            Dim firstTokenAt As DateTime? = Nothing
            Dim lastTokenAt As DateTime = DateTime.UtcNow
            Dim endThinking = Sub()
                                  If thinkingSince.HasValue AndAlso show Then
                                      If _state.ShowThinking Then
                                          thinking.Reset()
                                      Else
                                          Out.ClearPanel()
                                          Dim secs = Math.Max(1, CInt(Math.Round((DateTime.UtcNow - thinkingSince.Value).TotalSeconds)))
                                          Dim words = CountWords(thought.ToString())
                                          If words > 0 Then
                                              Out.Line(Ansi.Muted($"{Ansi.Therefore} Thought for {secs}s · {words:N0} word{If(words = 1, "", "s")}") &
                                                       If(_thoughtHintShown, "", Ansi.Dim2("  (/thoughts to read)")))
                                              _thoughtHintShown = True
                                          End If
                                      End If
                                  End If
                                  If thought.ToString().Trim().Length > 0 Then result.Thoughts.Add(thought.ToString().Trim())
                                  thought.Clear()
                                  thinkingSince = Nothing
                              End Sub
            Dim workerCts As CancellationTokenSource = Nothing
            ' Tool calls running in the background (a round's subagents and reads, with Parallel on).
            Dim pending As New List(Of Task)
            Try
                Dim body = BuildBody(o)
                Dim doneSeen = False, endedSeen = False
                Dim events = 0L
                ' One event of the answer, from the message's own stream or (following another window's answer) the live stream.
                Dim handle As Func(Of String, JsonElement, Task) =
                    Async Function(evName As String, d As JsonElement) As Task
                Select Case evName
                    Case "ended"
                        ' A reconnect found no answer to follow (it ended long ago, or the server restarted).
                        endedSeen = True
                    Case "user"
                        result.UserMessageId = Jx.StrOf(d, "id", Nothing)
                        If o.Follow Then
                            result.Prompt = Jx.StrOf(d, "content", "")
                            ' The goal's automatic continuation is written by the server, not sent from another window.
                            result.AutoGoal = Jx.StrOf(d, "finishReason", "") = "auto_goal"
                            If show Then
                                Out.StopSpinner()
                                md.Finish() : md.Reset()
                                If result.AutoGoal Then
                                    Out.Line(Ansi.Accent("↻") & " " & Ansi.Muted(AutoGoalLine(result.Prompt)))
                                Else
                                    Session.EchoUser(result.Prompt)
                                    Out.Line(Ansi.Muted("  " & Ansi.Elbow & "  sent from another window (web app)"))
                                End If
                                Out.StartSpinner(word, Function() SpinnerMeta())
                            End If
                        End If
                    Case "steers_left"
                        For Each t In Jx.ArrOf(d, "texts")
                            If t.ValueKind = JsonValueKind.String Then result.SteersLeft.Add(t.GetString())
                        Next
                    Case "start"
                        result.MessageId = Jx.StrOf(d, "messageId", Nothing)
                    Case "route"
                        result.RoutedModel = Jx.StrOf(d, "model")
                        If show Then
                            md.Reset()
                            Out.Line(Ansi.Muted($"  ↳ {Jx.StrOf(d, "label")} → {Jx.StrOf(d, "model")}"))
                        End If
                    Case "status"
                        Dim text = Jx.StrOf(d, "text")
                        If show AndAlso Not IsGeneric(text) Then Out.SpinnerText(text.TrimEnd("…"c, "."c) & "…")
                    Case "reasoning"
                        Dim text = Jx.StrOf(d, "text")
                        If o.Depth > 0 AndAlso o.Render Then _subView.Write(o.ChatId, o.Label, text, thinking:=True)
                        If Not thinkingSince.HasValue Then thinkingSince = DateTime.UtcNow
                        chars += text.Length
                        If o.Depth = 0 Then StreamedChars += text.Length : NoteRate()
                        firstTokenAt = If(firstTokenAt, DateTime.UtcNow) : lastTokenAt = DateTime.UtcNow
                        thought.Append(text)
                        If show AndAlso _state.ShowThinking Then
                            thinking.Append(text)
                        ElseIf show Then
                            Out.SetPanel(ThinkingPanel(thought.ToString()))
                        End If
                    Case "delta"
                        Dim text = Jx.StrOf(d, "text")
                        If o.Depth > 0 AndAlso o.Render Then _subView.Write(o.ChatId, o.Label, text, thinking:=False)
                        ' It writes: its thinking folds up (a bare line break between thoughts does not count).
                        If text.Trim().Length > 0 Then endThinking()
                        If o.Depth = 0 Then NoteRate(text.Length)
                        firstTokenAt = If(firstTokenAt, DateTime.UtcNow) : lastTokenAt = DateTime.UtcNow
                        result.Text.Append(text)
                        chars += text.Length
                        If o.Depth = 0 Then StreamedChars += text.Length
                        If show Then md.Append(text)
                        o.Observer?.Invoke("text", text)
                    Case "sources"
                        If show Then
                            md.Finish()
                            md.Reset()
                            ShowSources(d)
                        End If
                    Case "tool"
                        Dim callEl = Jx.Prop(d, "call")
                        ' A subagent's step ends where it acts: what it wrote so far goes into the scrollback first.
                        If o.Depth > 0 AndAlso o.Render AndAlso Jx.StrOf(d, "phase") = "start" Then _subView.Flush(o.ChatId)
                        If Jx.StrOf(callEl, "server") = "client" Then Return
                        endThinking()
                        ServerToolEvent(Jx.StrOf(d, "phase"), callEl, o, md)
                    Case "approval"
                        endThinking()
                        md?.Finish()
                        md?.Reset()
                        Await HandleApprovalAsync(o, d, toolMap, approved, ct).ConfigureAwait(False)
                    Case "client_tool"
                        If o.Depth > 0 AndAlso o.Render Then _subView.Flush(o.ChatId)
                        endThinking()
                        md?.Finish()
                        md?.Reset()
                        Dim background = Await DispatchClientToolAsync(o, d, toolMap, approved, result, pending, ct).ConfigureAwait(False)
                        If show Then Out.StartSpinner(If(background OrElse Not _running.IsEmpty, ParallelSpinnerText(), WorkingWord()), Function() SpinnerMeta())
                    Case "subagent"
                        ' A subagent the server runs (an answer started in the web app). The CLI's own subagents show from their own streams.
                        If o.Render AndAlso Not Jx.BoolOf(d, "relayed") Then ServerSubagentEvent(d)
                    Case "question"
                        For Each q In Jx.ArrOf(d, "questions")
                            result.Questions.Add(q.Clone())
                        Next
                    Case "goal"
                        result.GoalStatus = Jx.StrOf(d, "status", Nothing)
                        result.GoalSummary = Jx.StrOf(d, "summary", Nothing)
                        If show Then
                            md.Finish() : md.Reset()
                            Dim gs = result.GoalStatus
                            Dim gLabel = If(gs = "met", "met.", If(gs = "failed", "could not be met.", If(gs = "stopped", "cleared (you asked to stop).", "paused (not met yet).")))
                            Out.Line(Ansi.Muted($"{Ansi.Star} Goal " & gLabel & If(String.IsNullOrWhiteSpace(result.GoalSummary), "", " " & ToolKit.OneLine(result.GoalSummary, 120))))
                        End If
                    Case "rollback"
                        ' A failed round is retried, or the server cut a runaway repetition: start a clean line so the
                        ' re-sent text does not run into the partial. Printed text cannot be taken back here, so say so.
                        Dim dropped = Jx.IntOf(d, "drop")
                        result.Text.Length = Math.Max(0, result.Text.Length - dropped)
                        thought.Length = Math.Max(0, thought.Length - Jx.IntOf(d, "dropReasoning"))
                        If show AndAlso thinkingSince.HasValue AndAlso Not _state.ShowThinking Then Out.SetPanel(If(thought.Length > 0, ThinkingPanel(thought.ToString()), Nothing))
                        If show Then
                            md.Finish() : md.Reset()
                            If dropped >= 200 Then Out.Line(Ansi.Muted($"{Ansi.Star} {dropped:N0} characters above were taken back (the model repeated itself or the round was retried); the saved answer leaves them out."))
                        End If
                    Case "compacted"
                        _state.Hooks?.Run("PostCompact", "auto", New Dictionary(Of String, Object) From {{"trigger", "auto"}})
                        If show Then
                            md.Finish()
                            md.Reset()
                            Dim st = CompactStats.From(Jx.Prop(d, "stats"))
                            If st IsNot Nothing Then
                                Out.Line(Ansi.Muted($"{Ansi.Star} " & st.Line()))
                            Else
                                Out.Line(Ansi.Muted($"{Ansi.Star} Conversation compacted to fit the context window ({Jx.DblOf(d, "percent"):0}% used now)"))
                            End If
                        End If
                    Case "error"
                        result.Error = Jx.StrOf(d, "message", "The answer failed.")
                    Case "done"
                        doneSeen = True
                        Dim msg = Jx.Prop(d, "message")
                        result.PromptTokens = Jx.IntOf(msg, "promptTokens")
                        result.CompletionTokens = Jx.IntOf(msg, "completionTokens")
                        result.CostMicros = Jx.LongOf(msg, "costMicros")
                        If result.MessageId Is Nothing Then result.MessageId = Jx.StrOf(msg, "id", Nothing)
                        ' For `npx ccusage` (Claude Code's log format); "ccusageLog": false in settings turns it off.
                        If _state.Settings.Bool("ccusageLog", True) Then UsageLog.Record(o.ChatId, _state.ProjectDir, msg, Jx.LongOf(d, "cachedTokens"))
                        result.Context = ContextInfo.From(Jx.Prop(d, "context"))
                        result.StopReason = Jx.StrOf(d, "stopReason", "end")
                        result.Rounds = Jx.IntOf(d, "rounds")
                        Dim t = Jx.StrOf(d, "title", Nothing)
                        If Not String.IsNullOrWhiteSpace(t) Then result.Title = t
                        If Not String.IsNullOrEmpty(Jx.StrOf(msg, "error", Nothing)) AndAlso result.Error Is Nothing Then result.Error = Jx.StrOf(msg, "error")
                        ' The answer is complete; the server may still send a better title.
                        If show Then
                            md.Finish()
                            Out.StopSpinner()
                        End If
                    Case "title"
                        result.Title = Jx.StrOf(d, "title", result.Title)
                End Select
                    End Function
                ' Reads one response's events: the message's own stream, or a reconnect to it after a drop (GET …/attach).
                Dim pump = Async Function(resp As HttpResponseMessage) As Task
                    Using resp
                    Using stream = Await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(False)
                        Dim reader As New SseReader(stream)
                        While True
                            Dim ev = Await reader.NextAsync(ct).ConfigureAwait(False)
                            If Not ev.HasValue Then Exit While
                            events += 1
                            Dim d = ev.Value.Json()
                            Await handle(ev.Value.Name, d).ConfigureAwait(False)
                        End While
                    End Using
                    End Using
                End Function
                If o.Follow Then
                    Await FollowAsync(o, handle, toolMap, approved, result, md, show, word, Function() doneSeen, Sub() doneSeen = True, Sub() endedSeen = True, ct).ConfigureAwait(False)
                    If ct.IsCancellationRequested Then result.Cancelled = True
                End If
                ' This CLI's own answer: a tool call can also come over the live stream (its stream dropped and the server
                ' handed the call to this computer), so those run too while the answer lasts.
                workerCts = CancellationTokenSource.CreateLinkedTokenSource(ct)
                Dim worker = If(Link Is Nothing OrElse o.Follow OrElse o.Depth > 0, Task.CompletedTask, Task.Run(
                    Async Function()
                        While Not workerCts.IsCancellationRequested
                            Dim item = Await Link.ReadAsync(workerCts.Token, includeEvents:=False).ConfigureAwait(False)
                            If item Is Nothing Then Exit While
                            Await DispatchClientToolAsync(o, item.Data, toolMap, approved, result, pending, workerCts.Token, item.ChatId).ConfigureAwait(False)
                        End While
                    End Function))
                Dim lost As Exception = Nothing
                Try
                    If Not o.Follow Then Await pump(Await _chat.OpenMessageStreamAsync(o.ChatId, body, ct).ConfigureAwait(False)).ConfigureAwait(False)
                Catch ex As Exception When events > 0 AndAlso IsConnectionLoss(ex, ct)
                    lost = ex
                End Try
                ' The stream ended without the answer's end (a network drop, a proxy reconnect, a sleeping laptop): the answer
                ' keeps running on the server, so reconnect to it. Only after every attempt fails is this an error.
                Dim attempt = 0
                While Not o.Follow AndAlso Not doneSeen AndAlso Not endedSeen AndAlso result.Error Is Nothing AndAlso Not ct.IsCancellationRequested AndAlso events > 0
                    attempt += 1
                    If attempt > ReconnectAttempts Then
                        result.Error = "Connection to the server was lost" & If(lost Is Nothing, "", ": " & lost.Message) & $" (gave up after {ReconnectAttempts} reconnect attempts)."
                        Exit While
                    End If
                    If show Then
                        md.Finish() : md.Reset()
                        Out.StartSpinner($"Connection dropped: reconnecting (attempt {attempt} of {ReconnectAttempts})", Function() SpinnerMeta())
                    End If
                    Await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, 2 ^ attempt)), ct).ConfigureAwait(False)
                    Dim before = events
                    Try
                        Await pump(Await _chat.OpenAttachStreamAsync(o.ChatId, ct).ConfigureAwait(False)).ConfigureAwait(False)
                    Catch ex As Exception When IsConnectionLoss(ex, ct)
                        lost = ex
                    End Try
                    ' It streamed again before dropping: a fresh drop, not a failed attempt.
                    If events > before + 1 AndAlso Not doneSeen Then attempt = 0
                End While
                If endedSeen AndAlso Not doneSeen AndAlso result.Error Is Nothing Then
                    ' The answer is no longer on the server: take what was saved, or ask again (it was lost in a restart).
                    Dim conv = Await _chat.GetAsync(o.ChatId, ct).ConfigureAwait(False)
                    Dim last = conv.Messages.LastOrDefault()
                    If last IsNot Nothing AndAlso last.Role = "assistant" Then
                        Dim saved = If(last.Content, "")
                        If show AndAlso saved.StartsWith(result.Text.ToString(), StringComparison.Ordinal) Then md.Append(saved.Substring(result.Text.Length))
                        result.Text.Clear().Append(saved)
                        If result.MessageId Is Nothing Then result.MessageId = last.Id
                    ElseIf o.Follow Then
                        ' Nothing was saved for the other window's message (it was stopped before writing anything).
                    ElseIf o.LostRetries < 3 Then
                        If show Then
                            md.Finish() : md.Reset()
                            Out.Line(Ansi.Muted($"{Ansi.Star} The answer was lost before it was saved (the server restarted): answering again (attempt {o.LostRetries + 1} of 3)…"))
                        End If
                        o.LostRetries += 1
                        retryLost = True
                    Else
                        result.Error = "The answer was lost several times (the server restarted). Type /retry to answer it again."
                    End If
                End If
            Catch ex As OperationCanceledException When ct.IsCancellationRequested
                result.Cancelled = True
            Catch ex As IOException When ct.IsCancellationRequested
                result.Cancelled = True
            Catch ex As ApiException
                result.Error = ex.Message
            Catch ex As HttpRequestException
                result.Error = "Connection to the server was lost: " & ex.Message
            Catch ex As IOException
                result.Error = "Connection to the server was lost: " & ex.Message
            Finally
                workerCts?.Cancel()
            End Try
            ' Background tool calls answer before the answer ends; wait for any still finishing (an interrupted turn cancels them).
            Dim still As Task()
            SyncLock pending
                still = pending.ToArray()
            End SyncLock
            If still.Length > 0 Then
                Try
                    Await Task.WhenAll(still).ConfigureAwait(False)
                Catch ex As Exception When TypeOf ex Is OperationCanceledException OrElse TypeOf ex Is ApiException OrElse TypeOf ex Is HttpRequestException OrElse TypeOf ex Is IOException
                End Try
            End If
            ' A subagent's turn is over: its report is printed (shortly) and it leaves the panel.
            If o.Depth > 0 AndAlso o.Render AndAlso Not retryLost Then _subView.Finish(o.ChatId)
            If retryLost Then
                If show Then
                    md.Finish()
                    thinking.Finish()
                    Out.StopSpinner()
                End If
                Dim again = o.Regenerate
                o.Regenerate = True
                Try
                    Return Await RunAsync(o, ct).ConfigureAwait(False)
                Finally
                    o.Regenerate = again
                End Try
            End If
            If result.Cancelled AndAlso o.ChatId IsNot Nothing Then
                Dim unused = _chat.StopAsync(o.ChatId)
            End If
            ' Still thinking when it ended (stopped, or no answer text): the thought folds up all the same.
            endThinking()
            If show Then
                md.Finish()
                thinking.Finish()
                Out.ClearPanel()
                Out.StopSpinner()
            End If
            result.DurationMs = sw.ElapsedMilliseconds
            If result.CompletionTokens = 0 Then result.CompletionTokens = chars \ 4
            If firstTokenAt.HasValue Then
                Dim genSecs = (lastTokenAt - firstTokenAt.Value).TotalSeconds
                If genSecs >= 0.5 Then result.TokensPerSecond = result.CompletionTokens / genSecs
            End If
            If Not String.IsNullOrEmpty(_state.PendingFollowUp) Then
                result.FollowUp = _state.PendingFollowUp
                _state.PendingFollowUp = Nothing
            End If
            ' Stop hook: a hook that exits with 2 keeps the agent going with its message.
            If Not o.Follow AndAlso Not result.Cancelled AndAlso result.Error Is Nothing AndAlso result.FollowUp Is Nothing AndAlso _state.Hooks IsNot Nothing Then
                Dim h = _state.Hooks.Run(If(o.Depth = 0, "Stop", "SubagentStop"), "", New Dictionary(Of String, Object) From {{"stop_hook_active", o.StopHookActive}})
                If h.Blocked AndAlso o.Depth = 0 AndAlso Not o.StopHookActive Then
                    result.FollowUp = h.Reason
                    result.FollowUpFromStopHook = True
                End If
            End If
            Return result
        End Function

        ''' <summary>
        ''' Follows an answer written for another window in this chat (a message sent from the web app): its events come from
        ''' the live stream, its tool calls for this CLI run here, until it ends. Esc stops it (like the stop button there).
        ''' </summary>
        Private Async Function FollowAsync(o As TurnOptions, handle As Func(Of String, JsonElement, Task), toolMap As Dictionary(Of String, ITool), approved As HashSet(Of String),
                                           result As TurnResult, md As MarkdownStream, show As Boolean, word As String, isDone As Func(Of Boolean), markDone As Action,
                                           markEnded As Action, ct As CancellationToken) As Task
            If Link Is Nothing Then Return
            If o.ToolsOnly Then
                ' Tool calls for this CLI from a chat that is not open here (its answer is shown in the web app).
                While Not ct.IsCancellationRequested
                    Dim call1 = Await Link.ReadAsync(ct, 2000, includeEvents:=False).ConfigureAwait(False)
                    If call1 Is Nothing Then Exit While
                    Await HandleClientToolAsync(o, call1.Data, toolMap, approved, result, ct, call1.ChatId).ConfigureAwait(False)
                End While
                Return
            End If
            If Link.TakeCatchUp() Then
                ' It was already running when this CLI looked: show what it wrote so far, then the rest live.
                Try
                    Dim snap = Await _chat.LiveAsync(o.ChatId, ct).ConfigureAwait(False)
                    If Jx.BoolOf(snap, "active") Then
                        result.MessageId = Jx.StrOf(snap, "messageId", Nothing)
                        If Jx.StrOf(snap, "auto", "") = "goal" Then result.AutoGoal = True
                        If show Then
                            Out.StopSpinner()
                            Out.Line(Ansi.Muted(If(result.AutoGoal, $"↻ Following the goal's automatic continuation (the goal is not met yet)…",
                                                   $"{Ansi.Star} Following an answer to a message sent from another window (web app)…")))
                            Out.StartSpinner(word, Function() SpinnerMeta())
                        End If
                        Dim soFar = Jx.StrOf(snap, "content")
                        result.Text.Append(soFar)
                        If show AndAlso soFar.Length > 0 Then md.Append(soFar)
                        For Each a In Jx.ArrOf(snap, "approvals")
                            Await handle("approval", a.Clone()).ConfigureAwait(False)
                        Next
                    End If
                Catch ex As ApiException
                End Try
            End If
            While Not ct.IsCancellationRequested
                ' After the answer is done only its title may follow; without events for a while, check that it still runs.
                Dim item = Await Link.ReadAsync(ct, If(isDone(), 3000, 30000)).ConfigureAwait(False)
                If ct.IsCancellationRequested Then Exit While
                If item Is Nothing Then
                    If isDone() Then Exit While
                    Dim snap As JsonElement
                    Try
                        snap = Await _chat.LiveAsync(o.ChatId, ct).ConfigureAwait(False)
                    Catch ex As ApiException
                        Continue While
                    End Try
                    If Not Jx.BoolOf(snap, "active") Then
                        markEnded()
                        Exit While
                    End If
                    Continue While
                End If
                If item.Type = "client_tool" Then
                    md?.Finish()
                    md?.Reset()
                    Await HandleClientToolAsync(o, item.Data, toolMap, approved, result, ct, item.ChatId).ConfigureAwait(False)
                    If show AndAlso Not isDone() Then Out.StartSpinner(WorkingWord(), Function() SpinnerMeta())
                    Continue While
                End If
                If item.ChatId <> o.ChatId Then Continue While
                If item.Type = "run" Then
                    If Jx.StrOf(item.Data, "phase") = "start" Then Continue While
                    If Not isDone() Then markEnded()
                    Exit While
                End If
                Await handle(item.Type, item.Data).ConfigureAwait(False)
            End While
        End Function

        ''' <summary>How often a dropped answer stream is reconnected (waits 2, 4, 8, 16 then 30 s) before it counts as lost.</summary>
        Private Const ReconnectAttempts As Integer = 8

        ''' <summary>The connection to the server failed (not the user cancelling): worth reconnecting.</summary>
        Private Shared Function IsConnectionLoss(ex As Exception, ct As CancellationToken) As Boolean
            If ct.IsCancellationRequested Then Return False
            Dim api = TryCast(ex, ApiException)
            If api IsNot Nothing Then Return api.Code = "UNREACHABLE" OrElse api.Status >= 500
            Return TypeOf ex Is HttpRequestException OrElse TypeOf ex Is IOException OrElse TypeOf ex Is OperationCanceledException
        End Function

        Private Function SpinnerMeta() As String
            Dim tokens = StreamedChars \ 4
            Dim rate = CurrentRate()
            Return PlanGauge(_state.Todos) & If(tokens > 0, "↓ " & FormatTokens(tokens) & " tokens · ", "") & If(rate >= 1, $"{rate:0} tok/s · ", "") & KeyBindings.ShortLabel("stop") & " to interrupt"
        End Function

        ''' <summary>
        ''' The plan's progress on the working line while a task list is open ("▰▰▰▱▱ 3/5 · "): it is read on every frame, so it
        ''' fills as steps are completed. Nothing when there is no list, or every step is done.
        ''' </summary>
        Friend Shared Function PlanGauge(todos As List(Of TodoItem)) As String
            If todos Is Nothing OrElse todos.Count = 0 Then Return ""
            Dim list = todos.ToList()
            Dim done = list.Where(Function(t) t.Status = "completed").Count()
            If done = list.Count Then Return ""
            Const cells As Integer = 5
            Dim filled = CInt(Math.Floor(cells * done / CDbl(list.Count)))
            If done > 0 AndAlso filled = 0 Then filled = 1
            Return Ansi.Accent(New String("▰"c, filled)) & Ansi.Dim2(New String("▱"c, cells - filled)) & Ansi.Muted($" {done}/{list.Count} · ")
        End Function

        ''' <summary>Notes the streamed characters now (the speed is measured over the last few seconds).</summary>
        Private Sub NoteRate(Optional extra As Integer = 0)
            SyncLock _rate
                _rate.Enqueue((DateTime.UtcNow, StreamedChars + extra))
                While _rate.Count > 0 AndAlso (DateTime.UtcNow - _rate.Peek().At).TotalSeconds > 4
                    _rate.Dequeue()
                End While
            End SyncLock
        End Sub

        ''' <summary>Tokens per second over the last few seconds (about 4 characters a token); 0 when nothing arrives.</summary>
        Private Function CurrentRate() As Double
            SyncLock _rate
                If _rate.Count < 2 Then Return 0
                Dim first = _rate.Peek(), last = _rate.Last()
                If (DateTime.UtcNow - last.At).TotalSeconds > 3 Then Return 0
                Dim secs = Math.Max(0.5, (DateTime.UtcNow - first.At).TotalSeconds)
                Return (last.Chars - first.Chars) / 4.0 / secs
            End SyncLock
        End Function

        Private Shared Function CountWords(text As String) As Integer
            Return If(text, "").Split({" "c, ChrW(10), ChrW(13), ChrW(9)}, StringSplitOptions.RemoveEmptyEntries).Length
        End Function

        ''' <summary>The live thinking above the spinner: a header and its last lines, wrapped to the terminal.</summary>
        Private Shared Function ThinkingPanel(text As String) As List(Of String)
            Dim w = Math.Max(30, Math.Min(Ansi.TermWidth() - 1, 120))
            If text.Length > 6000 Then text = text.Substring(text.Length - 6000)
            Dim lines As New List(Of String)
            For Each para In text.Replace(ChrW(13), "").Split(ChrW(10))
                If para.Trim().Length = 0 Then Continue For
                lines.AddRange(Ansi.WrapText(para.Trim(), w, "  ", "  "))
            Next
            Dim panel = lines.Skip(Math.Max(0, lines.Count - 6)).Select(Function(l) Ansi.Muted(Ansi.Italic(l))).ToList()
            panel.Insert(0, Ansi.Muted(Ansi.Therefore & " Thinking…"))
            Return panel
        End Function

        Private Shared Function IsGeneric(text As String) As Boolean
            Dim t = If(text, "").Trim().TrimEnd("…"c, "."c).ToLowerInvariant()
            Return t = "" OrElse t = "thinking" OrElse t = "writing"
        End Function

        Private Shared Sub ShowSources(d As JsonElement)
            Dim items = If(d.ValueKind = JsonValueKind.Array, d.EnumerateArray().ToList(), Jx.ArrOf(d, "sources"))
            If items.Count = 0 Then Return
            Dim parts = items.Take(6).Select(Function(s, i) $"[{i + 1}] " & Ansi.Text(ToolKit.OneLine(Jx.StrOf(s, "title", Jx.StrOf(s, "url")), 50)) & Ansi.Dim2(" " & PermissionEngine.HostOf(Jx.StrOf(s, "url")))).ToList()
            Out.Line(Ansi.Truncate(Ansi.Muted("  " & Ansi.Elbow & "  Sources: ") & String.Join(Ansi.Muted(" · "), parts) & If(items.Count > 6, Ansi.Muted($" +{items.Count - 6}"), ""), Ansi.TermWidth() - 1))
        End Sub

        ' ------------------------------------------------------------------ tools that run on the server (web search, server MCP, code interpreter)

        Private Sub ServerToolEvent(phase As String, callEl As JsonElement, o As TurnOptions, md As MarkdownStream)
            Dim name = Jx.StrOf(callEl, "name")
            Dim args = Jx.StrOf(callEl, "arguments")
            If name = "user_note" Then
                ' A message the user added while the answer ran (from the web app or here): the model read it at this point.
                If o.Render AndAlso o.Depth = 0 Then
                    Dim noteArgs As JsonElement
                    Dim said = If(Jx.TryParse(args, noteArgs), Jx.StrOf(noteArgs, "text", ""), "")
                    md?.Finish()
                    md?.Reset()
                    Out.StopSpinner()
                    Session.EchoUser(said)
                    Out.Line(Ansi.Muted("  " & Ansi.Elbow & "  added while Agentic was working"))
                    Out.StartSpinner(WorkingWord(), Function() SpinnerMeta())
                End If
                Return
            End If
            If phase = "start" Then
                If o.Render AndAlso o.Depth = 0 Then
                    md.Finish()
                    md.Reset()
                    Out.SpinnerText(PrettyServerTool(name) & "…")
                End If
                o.Observer?.Invoke("tool_use", New With {.name = name, .input = SafeJson(args), .server = Jx.StrOf(callEl, "server")})
                Return
            End If
            Dim ok = Jx.BoolOf(callEl, "ok", True)
            Dim res = Jx.StrOf(callEl, "result")
            ' A task list the server keeps (update_todos) is the plan too: the working line's gauge and ctrl+t show it.
            If ok AndAlso o.Depth = 0 AndAlso (name = "update_todos" OrElse name = "TodoWrite") Then
                Dim todoArgs As JsonElement
                If Jx.TryParse(args, todoArgs) Then
                    Dim items = Jx.ArrOf(todoArgs, "todos")
                    If items.Count > 0 Then
                        _state.Todos = items.Select(Function(t) New TodoItem With {.Content = Jx.StrOf(t, "content", ""), .Status = Jx.StrOf(t, "status", "pending"),
                                                                                    .ActiveForm = Jx.StrOf(t, "activeForm", Jx.StrOf(t, "content", ""))}).ToList()
                    End If
                End If
            End If
            o.Observer?.Invoke("tool_result", New With {.name = name, .ok = ok, .content = res})
            If o.Depth > 0 Then
                If o.Render Then Out.Line("    " & Ansi.Muted(Ansi.Elbow & "  " & PrettyServerTool(name) & "(" & ToolKit.OneLine(ArgsSummary(args), 60) & ")"))
                Return
            End If
            If Not o.Render Then Return
            Dim dot = If(ok, Ansi.Ok(Ansi.Dot), Ansi.Bad(Ansi.Dot))
            Out.Line(dot & " " & Ansi.Bold(PrettyServerTool(name)) & Ansi.Muted("(" & ToolKit.OneLine(ArgsSummary(args), 90) & ")") & Ansi.Dim2("  on the server"))
            Dim first = ToolKit.OneLine(If(res, ""), Math.Max(20, Ansi.TermWidth() - 12))
            Out.Line(Ansi.Muted("  " & Ansi.Elbow & "  ") & If(ok, Ansi.Muted(If(first.Length = 0, "(no output)", first)), Ansi.Bad(first)))
        End Sub

        Private Shared Function PrettyServerTool(name As String) As String
            If name.StartsWith("mcp__") Then
                Dim parts = name.Substring(5).Split({"__"}, 2, StringSplitOptions.None)
                If parts.Length = 2 Then Return parts(0) & " - " & parts(1) & " (MCP)"
            End If
            Select Case name
                Case "web_search" : Return "Web Search"
                Case "run_code" : Return "Run code"
                Case Else : Return name
            End Select
        End Function

        Private Shared Function ArgsSummary(args As String) As String
            Dim e As JsonElement
            If Not Jx.TryParse(args, e) OrElse e.ValueKind <> JsonValueKind.Object Then Return If(args, "")
            Dim parts As New List(Of String)
            For Each p In e.EnumerateObject()
                Dim v = If(p.Value.ValueKind = JsonValueKind.String, """" & p.Value.GetString() & """", p.Value.GetRawText())
                parts.Add(p.Name & ": " & v)
                If parts.Count >= 3 Then Exit For
            Next
            Return String.Join(", ", parts)
        End Function

        Private Shared Function SafeJson(text As String) As Object
            Dim e As JsonElement
            If Jx.TryParse(text, e) Then Return e
            Return text
        End Function

        ' ------------------------------------------------------------------ approvals

        ''' <summary>The server asks before a tool runs: client tools go through the CLI's permission rules, server tools are asked.</summary>
        Private Async Function HandleApprovalAsync(o As TurnOptions, d As JsonElement, toolMap As Dictionary(Of String, ITool), approved As HashSet(Of String), ct As CancellationToken) As Task
            Dim id = Jx.StrOf(d, "id")
            Dim name = Jx.StrOf(d, "name")
            Dim argsText = Jx.StrOf(d, "arguments", "{}")
            Dim input As JsonElement
            If Not Jx.TryParse(argsText, input) Then input = Jx.Parse("{}")
            ' Answered in another window already (a followed answer, or the web app was quicker).
            If Link?.ResolvedChoice(id) IsNot Nothing Then Return
            ' This CLI's own question to the other windows (see AskUser): answered here.
            If id.StartsWith("cli-", StringComparison.Ordinal) Then Return
            Dim choice As String
            If Jx.StrOf(d, "server") = "client" Then
                Dim tool As ITool = Nothing
                If Not toolMap.TryGetValue(name, tool) Then
                    choice = "deny"
                Else
                    Dim r As (Allowed As Boolean, Message As String)
                    Try
                        r = Authorize(tool, input, o, callId:=id, serverApprovalId:=id)
                    Catch ex As Exception When Not TypeOf ex Is OperationCanceledException
                        r = (False, ex.Message)
                    End Try
                    If Link?.ResolvedChoice(id) IsNot Nothing Then
                        If r.Allowed OrElse Link.ResolvedChoice(id) <> "deny" Then
                            SyncLock approved
                                approved.Add(id)
                            End SyncLock
                        End If
                        Return
                    End If
                    choice = If(r.Allowed, "once", "deny")
                    If r.Allowed Then
                        SyncLock approved
                            approved.Add(id)
                        End SyncLock
                    End If
                End If
            Else
                choice = AskServerTool(name, argsText, Jx.StrOf(d, "reason"), o, id)
                If choice Is Nothing Then Return
            End If
            Try
                Await _chat.ApproveAsync(o.ChatId, id, choice, ct).ConfigureAwait(False)
            Catch ex As ApiException When ex.IsNotFound
                ' Answered in another window already.
            End Try
        End Function

        Private Function AskServerTool(name As String, argsText As String, reason As String, o As TurnOptions, Optional approvalId As String = Nothing) As String
            Dim input = Jx.Parse("{}")
            Dim deny = _perm.Rules("deny").FirstOrDefault(Function(r) r.Tool.Equals(name, StringComparison.OrdinalIgnoreCase))
            If deny IsNot Nothing Then Return "deny"
            If _state.Mode = PermissionMode.BypassPermissions Then Return "once"
            If _perm.Rules("allow").Any(Function(r) r.Tool.Equals(name, StringComparison.OrdinalIgnoreCase)) Then Return "once"
            If _state.Mode = PermissionMode.Plan Then Return "deny"
            If Not _state.Interactive AndAlso _state.PermissionAsker IsNot Nothing Then
                Dim input2 As JsonElement
                If Not Jx.TryParse(argsText, input2) Then input2 = Jx.Parse("{}")
                Dim a = _state.PermissionAsker.Invoke(name, input2, PrettyServerTool(name) & " (on the server)", reason)
                Return If(a.Allowed, If(a.Remember, "always", "once"), "deny")
            End If
            If Not _state.Interactive Then Return "deny"
            Out.StopSpinner()
            Notify($"Agentic needs your permission to use {name}")
            Dim preview = ToolKit.PreviewLines(PrettyJson(argsText), 16, "  ")
            DrawBox(PrettyServerTool(name) & " (runs on the server)", preview & Ansi.Muted("  Asked because " & reason))
            ' The web app may answer first: the question here then closes.
            Menu.Abort = If(Link Is Nothing OrElse approvalId Is Nothing, Nothing, New Func(Of Boolean)(Function() Link.ResolvedChoice(approvalId) IsNot Nothing))
            Dim pick As Integer
            Try
                pick = Menu.Select(" Do you want to proceed?", {New MenuOption("Yes"), New MenuOption("Yes, and don't ask again in this chat"), New MenuOption("No (esc)")})
            Finally
                Menu.Abort = Nothing
            End Try
            If Menu.Aborted Then
                Out.Line(Ansi.Muted("  " & Ansi.Elbow & "  " & If(Link.ResolvedChoice(approvalId) = "deny", "Denied", "Allowed") & " in another window"))
                Return Nothing
            End If
            If pick < 0 Then Interrupt?.Invoke()
            Return If(pick = 0, "once", If(pick = 1, "always", "deny"))
        End Function

        Private Shared Function PrettyJson(text As String) As String
            Dim e As JsonElement
            If Jx.TryParse(text, e) Then Return Jx.Stringify(e, True)
            Return text
        End Function

        ''' <summary>Decides (asking the user when the rules say so) whether a client tool may run.</summary>
        Friend Function Authorize(tool As ITool, ByRef input As JsonElement, o As TurnOptions, Optional callId As String = Nothing, Optional preApproved As Boolean = False,
                                  Optional serverApprovalId As String = Nothing) As (Allowed As Boolean, Message As String)
            Dim check = _perm.Evaluate(tool, input)
            Dim hookAsks = False
            If _state.Hooks IsNot Nothing AndAlso check.Verdict <> Verdict.Deny Then
                Dim h = _state.Hooks.Run("PreToolUse", tool.Name, New Dictionary(Of String, Object) From {{"tool_name", tool.Name}, {"tool_input", input}, {"tool_use_id", If(callId, "")}})
                If h.Blocked Then
                    ShowDenied(tool, input, "blocked by a hook: " & h.Reason, o)
                    Return (False, "A PreToolUse hook blocked this: " & h.Reason)
                End If
                ' updatedInput: the hook rewrote the arguments; the rules are checked again on what will run.
                If h.UpdatedInput.HasValue Then
                    input = h.UpdatedInput.Value
                    check = _perm.Evaluate(tool, input)
                End If
                If h.PermissionDecision = "allow" Then check = New PermissionCheck With {.Verdict = Verdict.Allow, .Reason = "allowed by a hook"}
                If h.PermissionDecision = "ask" Then
                    check = New PermissionCheck With {.Verdict = Verdict.Ask, .Reason = "a hook asks for confirmation"}
                    hookAsks = True
                End If
            End If
            Select Case check.Verdict
                Case Verdict.Allow
                    Return (True, Nothing)
                Case Verdict.Deny
                    ShowDenied(tool, input, check.Reason, o)
                    Return (False, $"Permission to use {tool.Name} was denied: {check.Reason}.")
            End Select
            ' The user already allowed this call in the chat (an approval answered in the web app): no second question here.
            If preApproved Then Return (True, Nothing)
            ' One question at a time, even with tools running in parallel (the hooks above ran outside this lock, so a slow hook
            ' does not hold up the other calls).
            SyncLock _askLock
                ' Answered "don't ask again" for another call while this one waited.
                If Not hookAsks AndAlso _perm.Evaluate(tool, input).Verdict = Verdict.Allow Then Return (True, Nothing)
                Return DecideAsk(tool, input, check, o, callId, serverApprovalId)
            End SyncLock
        End Function

        ''' <summary>A call the rules leave to the user: PermissionRequest hooks, the host program, or the question on the console.</summary>
        Private Function DecideAsk(tool As ITool, ByRef input As JsonElement, check As PermissionCheck, o As TurnOptions, callId As String,
                                   serverApprovalId As String) As (Allowed As Boolean, Message As String)
            ' PermissionRequest hooks answer the question before anyone is asked (behavior allow / deny).
            If _state.Hooks IsNot Nothing Then
                Dim pr = _state.Hooks.Run("PermissionRequest", tool.Name, New Dictionary(Of String, Object) From {{"tool_name", tool.Name}, {"tool_input", input}, {"reason", check.Reason}, {"tool_use_id", If(callId, "")}})
                If pr.UpdatedInput.HasValue Then input = pr.UpdatedInput.Value
                If pr.Blocked OrElse pr.PermissionDecision = "deny" Then
                    Dim why = If(pr.Reason, "denied by a PermissionRequest hook")
                    ShowDenied(tool, input, why, o)
                    Return (False, "A PermissionRequest hook denied this: " & why)
                End If
                If pr.PermissionDecision = "allow" Then Return (True, Nothing)
            End If
            If Not _state.Interactive AndAlso _state.PermissionAsker IsNot Nothing Then
                Dim a = _state.PermissionAsker.Invoke(tool.Name, input, tool.Title(input, _state), check.Reason)
                If a.Allowed Then
                    If a.Remember Then _perm.AllowForSession(PermissionRule.Parse(If(_perm.SuggestRule(tool.Name, input), tool.Name), "host"))
                    Return (True, Nothing)
                End If
                Return (False, "The user did not allow this tool use." & If(String.IsNullOrWhiteSpace(a.Message), " Do not try it again; continue without it or explain what you need.", " The user said: " & a.Message))
            End If
            If Not _state.Interactive Then
                ShowDenied(tool, input, check.Reason & " (no one to ask in this mode)", o)
                Return (False, $"Permission to use {tool.Name} was not granted: {check.Reason}, and this session cannot ask the user. " &
                               "Continue without it if possible; otherwise explain what you need. (The user can allow it with --allowedTools or a permission mode.)")
            End If
            Return AskUser(tool, input, check, o, callId, serverApprovalId)
        End Function

        Private Sub ShowDenied(tool As ITool, input As JsonElement, reason As String, o As TurnOptions)
            If Not o.Render Then Return
            Dim indent = If(o.Depth > 0, "    ", "")
            Out.Line(indent & Ansi.Bad(Ansi.Dot) & " " & StyleTitle(tool.Title(input, _state)))
            Out.Line(indent & Ansi.Muted("  " & Ansi.Elbow & "  ") & Ansi.Bad("Denied: " & reason))
        End Sub

        Private Const PartialEditNote As String = "Note: the user accepted only part of this change (they left some of it out); the file has exactly what they accepted. " &
                                                  "Read it again before changing it further, and do not redo what they left out unless they ask."

        ''' <summary>The file's text after an edit tool's change (LF line endings); Nothing when it cannot be worked out here.</summary>
        Private Function ProposedText(tool As ITool, input As JsonElement, before As String) As String
            Dim lf = before.Replace(vbCrLf, vbLf)
            Select Case tool.Name
                Case "Write"
                    Return Jx.StrOf(input, "content", "").Replace(vbCrLf, vbLf)
                Case "Edit"
                    Dim r = EditLogic.Apply(lf, Jx.StrOf(input, "old_string", ""), Jx.StrOf(input, "new_string", ""), Jx.BoolOf(input, "replace_all", False))
                    Return If(r.Error Is Nothing, r.NewText, Nothing)
                Case "MultiEdit"
                    Dim text = lf
                    For Each e In Jx.ArrOf(input, "edits")
                        Dim r = EditLogic.Apply(text, Jx.StrOf(e, "old_string", ""), Jx.StrOf(e, "new_string", ""), Jx.BoolOf(e, "replace_all", False))
                        If r.Error IsNot Nothing Then Return Nothing
                        text = r.NewText
                    Next
                    Return text
            End Select
            Return Nothing
        End Function

        ''' <summary>
        ''' /ide: an edit is reviewed in the editor (VS Code's diff: accept, reject, or revert single changes and accept the
        ''' rest). Nothing when the editor cannot show it (the question is then asked here); input becomes what the user kept.
        ''' </summary>
        Private Function IdeReview(tool As ITool, ByRef input As JsonElement, o As TurnOptions) As (Allowed As Boolean, Message As String)?
            Dim ide = _state.Ide
            Dim target = _perm.TargetPath(tool.Name, input)
            If ide Is Nothing OrElse target Is Nothing Then Return Nothing
            Dim exists = File.Exists(target)
            Dim before As String
            Try
                before = If(exists, File.ReadAllText(target), "")
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
                Return Nothing
            End Try
            Dim proposed = ProposedText(tool, input, before)
            If proposed Is Nothing Then Return Nothing
            Dim indent = If(o.Depth > 0, "    ", "")
            Out.Line(indent & Ansi.Accent(Ansi.Dot) & " " & StyleTitle(tool.Title(input, _state)))
            Out.Line(indent & Ansi.Muted($"  {Ansi.Elbow}  Review it in {ide.Info.IdeName}: Accept or Reject there (revert single changes in the diff's margin to keep only part of it)…"))
            Notify($"Agentic wants to change {Path.GetFileName(target)}: review it in {ide.Info.IdeName}")
            Dim answer As (Accepted As Boolean, Content As String)
            Try
                answer = ide.DiffAsync(target, proposed, CancellationToken.None).GetAwaiter().GetResult()
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is Net.Http.HttpRequestException OrElse TypeOf ex Is OperationCanceledException OrElse TypeOf ex Is JsonException
                Out.Warning($"{ide.Info.IdeName} did not show the change ({ex.Message}): asking here instead.")
                Return Nothing
            End Try
            If Not answer.Accepted Then
                Out.Line(indent & Ansi.Muted("  " & Ansi.Elbow & "  ") & Ansi.Bad("Rejected in " & ide.Info.IdeName))
                Return (False, "The user rejected this change in the editor. The tool use was rejected (the file was NOT changed). STOP what you are doing and wait for the user to tell you how to proceed.")
            End If
            Dim kept = If(answer.Content, proposed).Replace(vbCrLf, vbLf)
            If kept = proposed Then
                Out.Line(indent & Ansi.Muted("  " & Ansi.Elbow & "  Accepted in " & ide.Info.IdeName))
                Return (True, Nothing)
            End If
            ' Accepted in part: the call writes exactly what the user kept.
            Dim obj = JsonNode.Parse(input.GetRawText()).AsObject()
            Dim lf = before.Replace(vbCrLf, vbLf)
            Select Case tool.Name
                Case "Write"
                    obj("content") = kept
                Case "Edit"
                    obj("old_string") = lf
                    obj("new_string") = kept
                    obj("replace_all") = False
                Case "MultiEdit"
                    obj("edits") = New JsonArray(New JsonObject From {{"old_string", lf}, {"new_string", kept}})
            End Select
            input = Jx.Parse(obj.ToJsonString())
            Out.Line(indent & Ansi.Muted("  " & Ansi.Elbow & "  Accepted in part in " & ide.Info.IdeName))
            Return (True, PartialEditNote)
        End Function

        Private Function AskUser(tool As ITool, ByRef input As JsonElement, check As PermissionCheck, o As TurnOptions, Optional callId As String = Nothing,
                                 Optional serverApprovalId As String = Nothing) As (Allowed As Boolean, Message As String)
            Out.StopSpinner()
            ' /ide: an edit is reviewed as a diff in the editor.
            If _state.Ide IsNot Nothing AndAlso serverApprovalId Is Nothing AndAlso PermissionEngine.IsEditTool(tool.Name) AndAlso tool.Name <> "NotebookEdit" Then
                Dim reviewed = IdeReview(tool, input, o)
                If reviewed.HasValue Then Return reviewed.Value
            End If
            ' The same question goes to the web app (every window of the user): whoever answers first decides.
            Dim chatForApproval = If(String.IsNullOrEmpty(_state.ChatId), o.ChatId, _state.ChatId)
            Dim approvalId As String = Nothing
            Dim remote As Task(Of String) = Nothing
            If serverApprovalId Is Nothing AndAlso Link IsNot Nothing AndAlso Link.Connected AndAlso Not String.IsNullOrEmpty(chatForApproval) Then
                approvalId = "cli-" & If(String.IsNullOrEmpty(callId), Guid.NewGuid().ToString("N").Substring(0, 12), callId)
                remote = _chat.RequestApprovalAsync(chatForApproval, approvalId, tool.Name, input.GetRawText(), check.Reason, Link.DeviceName, CancellationToken.None)
            End If
            Dim elsewhere = Function() As String
                                If serverApprovalId IsNot Nothing Then Return Link?.ResolvedChoice(serverApprovalId)
                                If remote IsNot Nothing AndAlso remote.IsCompletedSuccessfully AndAlso remote.Result <> "timeout" Then Return remote.Result
                                Return Nothing
                            End Function
            Notify($"Agentic needs your permission to use {tool.Name}")
            Dim title As String
            Dim question As String
            Dim options As New List(Of MenuOption)
            Dim isEdit = PermissionEngine.IsEditTool(tool.Name)
            Dim suggestion = _perm.SuggestRule(tool.Name, input)
            Select Case True
                Case isEdit
                    Dim target = _perm.TargetPath(tool.Name, input)
                    Dim exists = target IsNot Nothing AndAlso File.Exists(target)
                    title = If(tool.Name = "Write" AndAlso Not exists, "Create file", "Edit file")
                    question = $" Do you want to {If(tool.Name = "Write" AndAlso Not exists, "create", "make this edit to")} {Ansi.Bold(_state.Display(If(target, "")))}?"
                    options.Add(New MenuOption("Yes"))
                    options.Add(New MenuOption("Yes, allow all edits during this session", Nothing, "acceptEdits", True))
                Case tool.Name = "Bash" OrElse tool.Name = "Monitor"
                    title = If(tool.Name = "Monitor", "Monitor command", "Bash command")
                    question = " Do you want to proceed?"
                    options.Add(New MenuOption("Yes"))
                    If suggestion IsNot Nothing Then options.Add(New MenuOption($"Yes, and don't ask again for {Ansi.Bold(suggestion.Substring(5).TrimEnd(")"c))} commands in {_state.Display(_state.ProjectDir)}", Nothing, "rule:" & suggestion))
                Case tool.Name = "WebFetch"
                    title = "Fetch"
                    question = $" Do you want to allow fetching {Ansi.Bold(PermissionEngine.HostOf(Jx.StrOf(input, "url", "")))}?"
                    options.Add(New MenuOption("Yes"))
                    options.Add(New MenuOption($"Yes, and don't ask again for {PermissionEngine.HostOf(Jx.StrOf(input, "url", ""))}", Nothing, "rule:" & suggestion))
                Case tool.Name.StartsWith("mcp__")
                    title = "Tool use"
                    question = " Do you want to proceed?"
                    options.Add(New MenuOption("Yes"))
                    options.Add(New MenuOption($"Yes, and don't ask again for {Ansi.Bold(tool.Name)}", Nothing, "rule:" & tool.Name))
                Case Else
                    title = "Tool use"
                    question = $" Do you want to allow {Ansi.Bold(tool.Title(input, _state))}?"
                    options.Add(New MenuOption("Yes"))
                    If suggestion IsNot Nothing Then options.Add(New MenuOption($"Yes, during this session ({suggestion})", Nothing, "session:" & suggestion))
            End Select
            options.Add(New MenuOption("No, and tell Agentic what to do differently (esc)", Nothing, "no"))

            Dim preview As String = Nothing
            Try
                preview = tool.Preview(input, _state)
            Catch ex As Exception
                ' A preview that cannot be drawn leaves only the title: the question is still asked.
            End Try
            DrawBox(title, Ansi.Bold("  " & StyleTitle(tool.Title(input, _state))) & Environment.NewLine & If(preview, "") & Ansi.Muted("  Asked because " & check.Reason))
            Menu.Abort = If(remote Is Nothing AndAlso (serverApprovalId Is Nothing OrElse Link Is Nothing), Nothing, New Func(Of Boolean)(Function() elsewhere() IsNot Nothing))
            Dim pick As Integer
            Try
                pick = Menu.Select(question, options)
            Finally
                Menu.Abort = Nothing
            End Try
            If Menu.Aborted Then
                Dim said = elsewhere()
                Dim ok = said = "once" OrElse said = "always"
                Out.Line(Ansi.Muted("  " & Ansi.Elbow & "  ") & If(ok, Ansi.Muted("Allowed in another window"), Ansi.Bad("Denied in another window")))
                If said = "always" Then _perm.AllowForSession(PermissionRule.Parse(If(suggestion, tool.Name), "session"))
                ' Allowed in the web app with some of the edit's changes left out: the call runs with what the user kept.
                Dim edited As String = Nothing
                Dim parsed As JsonElement
                If ok AndAlso approvalId IsNot Nothing AndAlso _chat.ApprovedArguments.TryRemove(approvalId, edited) AndAlso Jx.TryParse(edited, parsed) AndAlso parsed.ValueKind = JsonValueKind.Object Then
                    input = parsed
                    Return (True, PartialEditNote)
                End If
                If ok Then Return (True, Nothing)
                Return (False, "The user did not allow this tool use (answered in another window). Do not try it again; continue without it or ask the user what to do.")
            End If
            Dim value = If(pick >= 0, options(pick).Value, "no")
            If remote IsNot Nothing Then
                ' Answered here: the question closes in the other windows.
                Dim local = If(value = "no", "deny", If(value = "acceptEdits" OrElse value.StartsWith("rule:") OrElse value.StartsWith("session:"), "always", "once"))
                Dim unused = _chat.ApproveAsync(chatForApproval, approvalId, local, CancellationToken.None).ContinueWith(Function(t) t.Exception)
            End If
            If value = "acceptEdits" Then
                _state.Mode = PermissionMode.AcceptEdits
                Return (True, Nothing)
            End If
            If value.StartsWith("rule:") Then
                Dim rule = value.Substring(5)
                _perm.AllowForSession(PermissionRule.Parse(rule, "session"))
                Try
                    _state.Settings.AddToList("local", "permissions.allow", rule)
                Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
                End Try
                Return (True, Nothing)
            End If
            If value.StartsWith("session:") Then
                _perm.AllowForSession(PermissionRule.Parse(value.Substring(8), "session"))
                Return (True, Nothing)
            End If
            If value <> "no" Then Return (True, Nothing)
            Dim feedback As String = Nothing
            If pick >= 0 Then
                feedback = Menu.Ask(Ansi.Accent(Ansi.Pointer) & " What should Agentic do instead? (Enter to skip)")
            Else
                Interrupt?.Invoke()
            End If
            Dim msg = "The user doesn't want to proceed with this tool use. The tool use was rejected (eg. if it was a file edit, the new_string was NOT written to the file). " &
                      If(String.IsNullOrWhiteSpace(feedback), "STOP what you are doing and wait for the user to tell you how to proceed.", "The user said: " & feedback.Trim())
            Return (False, msg)
        End Function

        Friend Shared Sub DrawBox(title As String, body As String)
            Dim w = Math.Min(Ansi.TermWidth() - 1, 120)
            Dim b = Ansi.Box
            Dim top = b(0) & b(4) & " " & title & " " & New String(b(4)(0), Math.Max(2, w - Ansi.Width(title) - 5)) & b(1)
            Out.EnsureNewLine()
            Out.Line(Ansi.Warn(top))
            For Each l In body.TrimEnd(ChrW(10), ChrW(13)).Replace(ChrW(13), "").Split(ChrW(10))
                Out.Line(Ansi.Warn(b(5)) & " " & Ansi.Pad(Ansi.Truncate(l, w - 3), w - 3) & Ansi.Warn(b(5)))
            Next
            Out.Line(Ansi.Warn(b(2) & New String(b(4)(0), w - 2) & b(3)))
        End Sub

        ''' <summary>"Read(src/app.vb)" with the tool name in bold.</summary>
        Friend Shared Function StyleTitle(t As String) As String
            Dim i = t.IndexOf("("c)
            If i <= 0 Then Return Ansi.Bold(t)
            Return Ansi.Bold(t.Substring(0, i)) & Ansi.Muted(t.Substring(i))
        End Function

        Private Sub Notify(message As String)
            If Not _state.Interactive Then Return
            DesktopNotify.Alert(_state.Settings.Str("notifications", "auto"), "Agentic needs you", message)
            _state.Hooks?.Run("Notification", "", New Dictionary(Of String, Object) From {{"message", message}})
        End Sub

        ' ------------------------------------------------------------------ running a tool here

        ''' <summary>"Goal not met yet — continuing by itself (2 of 25)" for the server's "Keep working on the goal… (automatic continuation 2 of 25…)".</summary>
        Friend Shared Function AutoGoalLine(prompt As String) As String
            Dim m = Text.RegularExpressions.Regex.Match(If(prompt, ""), "automatic continuation (\d+) of (\d+)", Text.RegularExpressions.RegexOptions.IgnoreCase)
            Return "Goal not met yet — continuing by itself" & If(m.Success, $" ({m.Groups(1).Value} of {m.Groups(2).Value})", "")
        End Function

        ''' <summary>A tool that may run alongside the other calls of its round: a subagent, or one that only reads.</summary>
        Friend Shared Function RunsInParallel(tool As ITool) As Boolean
            Return tool IsNot Nothing AndAlso (tool.Name = "Task" OrElse tool.IsReadOnly)
        End Function

        ''' <summary>
        ''' Runs a tool call from the server. With Parallel on, a subagent (Task) or read-only call starts in the background and
        ''' the next event is read at once, so a round's parallel calls all run together; anything else runs here and now.
        ''' Returns True when it started in the background (it is then in pending).
        ''' </summary>
        Private Async Function DispatchClientToolAsync(o As TurnOptions, d As JsonElement, toolMap As Dictionary(Of String, ITool), approved As HashSet(Of String),
                                                       result As TurnResult, pending As List(Of Task), ct As CancellationToken, Optional chatId As String = Nothing) As Task(Of Boolean)
            Dim tool As ITool = Nothing
            If Parallel AndAlso toolMap.TryGetValue(Jx.StrOf(d, "name"), tool) AndAlso RunsInParallel(tool) Then
                If _agentGate Is Nothing Then
                    _agentGate = New SemaphoreSlim(Math.Max(1, MaxParallelAgents))
                    _readGate = New SemaphoreSlim(Math.Max(1, MaxParallelReads))
                End If
                Dim call1 = d.Clone()
                Dim gate = If(tool.Name = "Task", _agentGate, _readGate)
                Dim run = Task.Run(Async Function()
                                       Await gate.WaitAsync(ct).ConfigureAwait(False)
                                       Try
                                           Await HandleClientToolAsync(o, call1, toolMap, approved, result, ct, chatId, parallel:=True).ConfigureAwait(False)
                                       Finally
                                           gate.Release()
                                       End Try
                                   End Function)
                SyncLock pending
                    pending.Add(run)
                End SyncLock
                Return True
            End If
            Await HandleClientToolAsync(o, d, toolMap, approved, result, ct, chatId).ConfigureAwait(False)
            Return False
        End Function

        ''' <summary>The spinner while tools run in parallel: how many, and what.</summary>
        Private Function ParallelSpinnerText() As String
            Dim labels = _running.Values.ToList()
            If labels.Count = 0 Then Return WorkingWord()
            Return $"{labels.Count} running in parallel: {String.Join(", ", labels.Take(4))}{If(labels.Count > 4, $" +{labels.Count - 4}", "")}"
        End Function

        Private Async Function HandleClientToolAsync(o As TurnOptions, d As JsonElement, toolMap As Dictionary(Of String, ITool), approved As HashSet(Of String),
                                                     result As TurnResult, ct As CancellationToken, Optional chatId As String = Nothing,
                                                     Optional parallel As Boolean = False) As Task
            Dim id = Jx.StrOf(d, "id")
            Dim name = Jx.StrOf(d, "name")
            Dim input As JsonElement
            If Not Jx.TryParse(Jx.StrOf(d, "arguments", "{}"), input) OrElse input.ValueKind <> JsonValueKind.Object Then input = Jx.Parse("{}")
            Dim tool As ITool = Nothing
            Dim r As ToolResult
            If Not toolMap.TryGetValue(name, tool) Then
                r = ToolResult.Fail($"Unknown tool '{name}'. Available: {String.Join(", ", toolMap.Keys)}")
            Else
                Dim allowed As Boolean
                SyncLock approved
                    allowed = approved.Contains(id)
                End SyncLock
                Dim denial As String = Nothing
                If Not allowed Then
                    ' Authorize asks one question at a time, even with tools running in parallel.
                    Dim a As (Allowed As Boolean, Message As String)
                    Try
                        a = Authorize(tool, input, o, id, Jx.BoolOf(d, "approved"))
                    Catch ex As Exception When Not TypeOf ex Is OperationCanceledException
                        a = (False, $"The permission check for {name} failed: {ex.Message}")
                    End Try
                    allowed = a.Allowed
                    denial = a.Message
                End If
                o.Observer?.Invoke("tool_use", New With {.id = id, .name = name, .input = input})
                If Not allowed Then
                    r = ToolResult.Fail(denial)
                Else
                    r = Await ExecuteAsync(tool, input, o, ct, parallel, id, chatId).ConfigureAwait(False)
                    ' Allowed with a note (the user accepted only part of an edit): the model hears it with the result.
                    If Not String.IsNullOrEmpty(denial) AndAlso r.Ok Then r.Content = r.Content & vbLf & denial
                    SyncLock result
                        result.ToolUses += 1
                    End SyncLock
                End If
            End If
            o.Observer?.Invoke("tool_result", New With {.id = id, .name = name, .ok = r.Ok, .content = r.Content})
            ' The answer waits for this result: a request lost on the way is sent again, like the web app does.
            For attempt = 1 To 4
                Try
                    Await _chat.ToolResultAsync(If(chatId, o.ChatId), id, r.Ok, ToolKit.Clip(r.Content, 60000), CancellationToken.None, r.ImageMime, r.Image, r.ImageName, r.Images).ConfigureAwait(False)
                    Exit For
                Catch ex As Exception When TypeOf ex Is ApiException OrElse TypeOf ex Is HttpRequestException OrElse TypeOf ex Is IOException OrElse
                                           (TypeOf ex Is OperationCanceledException AndAlso Not ct.IsCancellationRequested)
                    Dim api = TryCast(ex, ApiException)
                    If attempt = 4 OrElse (api IsNot Nothing AndAlso api.Status > 0 AndAlso api.Status < 500) Then
                        If o.Render Then Out.Line(Ansi.Bad("  Could not send the tool result: " & ex.Message))
                        Exit For
                    End If
                End Try
                Await Task.Delay(1500 * attempt).ConfigureAwait(False)
            Next
        End Function

        Friend Async Function ExecuteAsync(tool As ITool, input As JsonElement, o As TurnOptions, ct As CancellationToken, Optional parallel As Boolean = False,
                                           Optional callId As String = Nothing, Optional chatId As String = Nothing) As Task(Of ToolResult)
            Dim title = tool.Title(input, _state)
            Dim showMain = o.Render AndAlso o.Depth = 0
            Dim runKey As String = Nothing
            If parallel AndAlso showMain Then
                ' Running alongside others: a subagent gets a one-line start (its steps follow, tagged with its name); the
                ' spinner names everything that runs; each result is printed whole when it is done.
                runKey = Guid.NewGuid().ToString("N")
                _running(runKey) = If(tool.Name = "Task", ToolKit.OneLine(Jx.StrOf(input, "description", "subagent"), 28), ToolKit.OneLine(title, 28))
                If tool.Name = "Task" Then
                    Out.Line(Ansi.Accent(Ansi.Diamond) & " " & StyleTitle(title) & Ansi.Muted("  started"))
                End If
                Out.StartSpinner(ParallelSpinnerText(), Function() SpinnerMeta())
            ElseIf showMain AndAlso tool.Name = "Task" Then
                ' The Task tool's line goes first; its subagent's steps appear under it.
                Out.Line(Ansi.Accent(Ansi.Dot) & " " & StyleTitle(title))
                Out.Line(Ansi.Muted("  " & Ansi.Elbow & "  " & ToolKit.OneLine(Jx.StrOf(input, "prompt", ""), Math.Max(30, Ansi.TermWidth() - 10))))
            End If
            If showMain AndAlso runKey Is Nothing Then
                Out.StartSpinner(If(tool.Name = "Task", "Working on " & ToolKit.OneLine(Jx.StrOf(input, "description", "the task"), 40), ToolKit.OneLine(title, 60)) & "…", Function() SpinnerMeta())
            End If
            Dim ctx As New ToolContext With {.State = _state, .Cancel = ct, .Depth = o.Depth, .RunSubagent = RunSubagent, .ApprovePlan = ApprovePlan,
                                             .ChatId = If(chatId, o.ChatId), .CallId = callId}
            Dim r As ToolResult
            Try
                r = Await tool.RunAsync(input, ctx).ConfigureAwait(False)
            Catch ex As OperationCanceledException
                r = ToolResult.Fail("Interrupted by the user.", "Interrupted")
            Catch ex As Exception
                ' Whatever a tool throws is its result: the model hears it and the answer goes on (otherwise the server would
                ' wait for this call's result, and a call running in parallel would end the CLI).
                r = ToolResult.Fail($"{tool.Name} failed: {ex.Message}")
            Finally
                If runKey IsNot Nothing Then
                    Dim gone As String = Nothing
                    _running.TryRemove(runKey, gone)
                End If
            End Try
            If r Is Nothing Then r = ToolResult.Fail("The tool returned nothing.")
            If _state.Hooks IsNot Nothing Then
                ' A failed tool call is PostToolUseFailure (with its error); a successful one PostToolUse.
                Dim h = _state.Hooks.Run(If(r.Ok, "PostToolUse", "PostToolUseFailure"), tool.Name, New Dictionary(Of String, Object) From {
                    {"tool_name", tool.Name}, {"tool_input", input}, {"tool_use_id", If(callId, "")}, {"tool_response", New With {.ok = r.Ok, .content = ToolKit.Clip(r.Content, 4000)}},
                    {"error", If(r.Ok, Nothing, ToolKit.Clip(r.Content, 2000))}})
                If h.Blocked Then r.Content &= vbLf & vbLf & "PostToolUse hook feedback: " & h.Reason
                If h.Context IsNot Nothing Then r.Content &= vbLf & vbLf & h.Context
            End If
            If o.Render Then
                RenderResult(tool, title, r, o.Depth, o.Label, parallel:=runKey IsNot Nothing)
                If runKey IsNot Nothing AndAlso Not _running.IsEmpty Then Out.StartSpinner(ParallelSpinnerText(), Function() SpinnerMeta())
            End If
            Return r
        End Function

        ''' <summary>A subagent the server runs: its stream in the terminal, like the CLI's own subagents.</summary>
        Private Sub ServerSubagentEvent(d As JsonElement)
            Dim key = "srv:" & Jx.StrOf(d, "id")
            Select Case Jx.StrOf(d, "kind")
                Case "start" : _subView.Start(key, Jx.StrOf(d, "label", "subagent"))
                Case "text" : _subView.Write(key, Nothing, Jx.StrOf(d, "text"), thinking:=False)
                Case "reasoning" : _subView.Write(key, Nothing, Jx.StrOf(d, "text"), thinking:=True)
                Case "tool"
                    If Jx.StrOf(d, "phase") = "start" Then
                        _subView.Flush(key)
                    Else
                        Dim c = Jx.Prop(d, "call")
                        _subView.ToolLine(key, (Jx.StrOf(c, "name") & " " & Jx.StrOf(c, "target")).Trim(), Jx.BoolOf(c, "ok"))
                    End If
                Case "end" : _subView.Finish(key)
            End Select
        End Sub

        Private Sub RenderResult(tool As ITool, title As String, r As ToolResult, depth As Integer, Optional label As String = Nothing, Optional parallel As Boolean = False)
            If depth > 0 Then
                ' A subagent's step; with several subagents at work, tagged with whose it is.
                Dim who = If(String.IsNullOrEmpty(label), "", Ansi.Accent("[" & label & "] "))
                Out.Line("    " & If(r.Ok, Ansi.Muted(Ansi.Elbow & "  "), Ansi.Bad(Ansi.Elbow & "  ")) & who & Ansi.Muted(Ansi.Truncate(title, Ansi.TermWidth() - 12 - Ansi.Width(If(label, "")))))
                Return
            End If
            Dim dot = If(r.Ok, Ansi.Ok(Ansi.Dot), Ansi.Bad(Ansi.Dot))
            ' Run in parallel, a subagent's result comes long after its start line: name it again.
            If tool.Name <> "Task" OrElse parallel Then Out.Line(dot & " " & StyleTitle(Ansi.Truncate(title, Ansi.TermWidth() - 4)))
            Dim summary = r.Summary
            If summary Is Nothing AndAlso r.Detail Is Nothing Then summary = ToolKit.OneLine(r.Content, Math.Max(20, Ansi.TermWidth() - 10))
            If Not r.Ok AndAlso r.Summary Is Nothing Then summary = Ansi.Bad(ToolKit.OneLine(r.Content, Math.Max(20, Ansi.TermWidth() - 10)))
            Dim elbow = Ansi.Muted("  " & Ansi.Elbow & "  ")
            If summary IsNot Nothing Then
                Out.Line(elbow & summary)
                If r.Detail IsNot Nothing Then Out.Write(r.Detail)
            ElseIf r.Detail IsNot Nothing Then
                ' The first detail line goes on the ⎿ line.
                Dim lines = r.Detail.TrimEnd(ChrW(10), ChrW(13)).Split(ChrW(10))
                Out.Line(elbow & lines(0).TrimStart())
                If lines.Length > 1 Then Out.Line(String.Join(ChrW(10), lines.Skip(1)).TrimEnd(ChrW(13)))
            End If
        End Sub

    End Class

End Namespace
