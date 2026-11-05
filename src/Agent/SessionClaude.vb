Option Strict On
Option Explicit On
Option Infer On

Imports System.Text
Imports System.Text.Json
Imports System.Text.Json.Nodes
Imports System.Threading
Imports AgenticCli.Core
Imports AgenticCli.Tools
Imports AgenticCli.Ui

Namespace Agent

    ''' <summary>
    ''' Claude Code (the local claude CLI) used from the chat, the same way the desktop app offers it. Pick the model
    ''' "claude-code" (or run /claude) and each message runs the claude CLI here: Claude keeps its own session, tools,
    ''' MCP servers, skills and CLAUDE.md. Plan mode and the reasoning effort carry over; permission prompts and
    ''' AskUserQuestion are answered right in the terminal.
    ''' </summary>
    Partial Friend NotInheritable Class Session

        Friend Const ClaudeModelId As String = "claude-code"

        Private _claude As ClaudeCode
        Private _claudeSession As String

        Friend Shared Function ClaudeAvailable() As Boolean
            Return ClaudeCode.IsInstalled()
        End Function

        Friend Shared Function IsClaudeModel(id As String) As Boolean
            Return String.Equals(id, ClaudeModelId, StringComparison.OrdinalIgnoreCase)
        End Function

        ''' <summary>The claude run options, read from settings (claude.*) like the desktop app's Claude settings.</summary>
        Private Function BuildClaudeOptions() As ClaudeCode.Options
            Dim cfg = State.Settings
            Dim o As New ClaudeCode.Options With {
                .Model = cfg.Str("claude.model", "default"),
                .Effort = If(_server.ReasoningEffort = "ultracode", "high", _server.ReasoningEffort),
                .PermissionMode = cfg.Str("claude.permissionMode", ClaudeModeFor(State.Mode)),
                .Cwd = If(cfg.Str("claude.cwd"), State.Cwd),
                .FallbackModel = cfg.Str("claude.fallbackModel"),
                .AppendSystemPrompt = cfg.Str("claude.appendSystemPrompt"),
                .MaxTurns = cfg.Int("claude.maxTurns", 0)}
            o.AddDirs.AddRange(State.ExtraDirs)
            o.AllowedTools.AddRange(cfg.Strings("claude.allowedTools"))
            o.DisallowedTools.AddRange(cfg.Strings("claude.disallowedTools"))
            Return o
        End Function

        Private Shared Function ClaudeModeFor(mode As PermissionMode) As String
            Select Case mode
                Case PermissionMode.Plan : Return "plan"
                Case PermissionMode.AcceptEdits : Return "acceptEdits"
                Case PermissionMode.BypassPermissions : Return "bypassPermissions"
                Case Else : Return "default"
            End Select
        End Function

        ''' <summary>Runs one message through the local claude CLI and renders it like the rest of the chat.</summary>
        Private Async Function RunClaudeAsync(prompt As String) As Task(Of TurnResult)
            If _claude Is Nothing Then _claude = New ClaudeCode()
            Dim result As New TurnResult()
            Dim show = Not _printMode
            Dim plan = State.Mode = PermissionMode.Plan
            Dim opts = BuildClaudeOptions()
            Dim md As MarkdownStream = If(show, New MarkdownStream(Sub(t) Out.Write(t)), Nothing)
            Dim thinking As MarkdownStream = If(show AndAlso State.ShowThinking, New MarkdownStream(Sub(t) Out.Write(t), Ansi.Muted(Ansi.Therefore & " ")) With {.Muted = True}, Nothing)
            Dim started = DateTime.UtcNow
            Dim runId = Guid.NewGuid().ToString("N")

            _runCts = New CancellationTokenSource()
            Dim stopKeys = WatchKeys(_runCts)
            If show Then Out.StartSpinner("Claude Code…")

            Try
                Await _claude.RunAsync(runId, prompt, _claudeSession, plan, opts,
                    Function(line, run) RenderClaudeLineAsync(line, run, md, thinking, result, show, _runCts.Token),
                    _runCts.Token).ConfigureAwait(False)
            Catch ex As OperationCanceledException
                result.Cancelled = True
            Catch ex As Exception
                result.Error = ex.Message
            Finally
                If show Then Out.StopSpinner()
                md?.Finish()
                thinking?.Finish()
            End Try
            Await stopKeys().ConfigureAwait(False)

            result.DurationMs = CLng((DateTime.UtcNow - started).TotalMilliseconds)
            _turns += 1
            If result.Text.Length > 0 Then _lastAnswer = result.Text.ToString()
            If Not _printMode Then
                If result.Cancelled Then
                    Out.Line(Ansi.Muted("  " & Ansi.Elbow & "  ") & Ansi.Bad("Interrupted"))
                ElseIf result.Error IsNot Nothing Then
                    Out.Fail(result.Error)
                End If
                Out.Line()
            End If
            Return result
        End Function

        ''' <summary>Renders one stream-json line from claude: text, thinking, tools, permission prompts and questions.</summary>
        Private Async Function RenderClaudeLineAsync(line As String, run As ClaudeCode.Run, md As MarkdownStream, thinking As MarkdownStream,
                                                     result As TurnResult, show As Boolean, ct As CancellationToken) As Task
            Dim doc As JsonElement
            If Not Jx.TryParse(line, doc) OrElse doc.ValueKind <> JsonValueKind.Object Then Return
            Dim type = Jx.StrOf(doc, "type")
            Dim sid = Jx.StrOf(doc, "session_id", Nothing)
            If Not String.IsNullOrWhiteSpace(sid) Then _claudeSession = sid

            Select Case type
                Case "system"
                    If Jx.StrOf(doc, "subtype") = "init" AndAlso show Then
                        Out.StopSpinner()
                        Dim model = Jx.StrOf(doc, "model", "claude")
                        Out.Line(Ansi.Muted($"  ↳ Claude Code → {model}{If(State.Mode = PermissionMode.Plan, "  (plan mode)", "")}"))
                        Out.StartSpinner("Claude Code…")
                    End If

                Case "stream_event"
                    Dim ev = Jx.Prop(doc, "event")
                    Dim evType = Jx.StrOf(ev, "type")
                    If evType = "content_block_delta" Then
                        Dim delta = Jx.Prop(ev, "delta")
                        Dim dType = Jx.StrOf(delta, "type")
                        If dType = "text_delta" Then
                            If show Then Out.StopSpinner() : md?.Append(Jx.StrOf(delta, "text"))
                            result.Text.Append(Jx.StrOf(delta, "text"))
                        ElseIf dType = "thinking_delta" Then
                            thinking?.Append(Jx.StrOf(delta, "thinking"))
                        End If
                    End If

                Case "assistant"
                    ' Tool calls Claude makes (text and thinking already streamed through stream_event).
                    Dim msg = Jx.Prop(doc, "message")
                    For Each block In Jx.ArrOf(msg, "content")
                        If Jx.StrOf(block, "type") = "tool_use" AndAlso show Then
                            md?.Finish() : md?.Reset()
                            thinking?.Finish() : thinking?.Reset()
                            Out.StopSpinner()
                            Dim name = Jx.StrOf(block, "name")
                            Dim args = ClaudeToolSummary(Jx.Prop(block, "input"))
                            Out.Line(Ansi.Accent(Ansi.Dot) & " " & Ansi.Bold(name) & Ansi.Muted(If(args.Length > 0, "(" & ToolKit.OneLine(args, 80) & ")", "")))
                            result.ToolUses += 1
                            Out.StartSpinner(name & "…")
                        End If
                    Next

                Case "user"
                    ' Tool results Claude got back; show a short confirmation line.
                    If show Then
                        Dim msg = Jx.Prop(doc, "message")
                        For Each block In Jx.ArrOf(msg, "content")
                            If Jx.StrOf(block, "type") = "tool_result" Then
                                Dim err = Jx.BoolOf(block, "is_error")
                                Dim dot = If(err, Ansi.Bad(Ansi.Dot), Ansi.Ok(Ansi.Dot))
                                Out.Line(Ansi.Muted("  " & Ansi.Elbow & "  ") & dot & Ansi.Muted(If(err, " tool failed", " done")))
                            End If
                        Next
                    End If

                Case "control_request"
                    Await HandleClaudeControlAsync(doc, run, show, ct).ConfigureAwait(False)

                Case "result"
                    md?.Finish()
                    thinking?.Finish()
                    If show Then Out.StopSpinner()
                    result.StopReason = Jx.StrOf(doc, "subtype", "end")
                    If Jx.BoolOf(doc, "is_error") Then result.Error = Jx.StrOf(doc, "result", "Claude Code reported an error.")
                    Dim usage = Jx.Prop(doc, "usage")
                    If usage.ValueKind = JsonValueKind.Object Then
                        result.PromptTokens = Jx.IntOf(usage, "input_tokens")
                        result.CompletionTokens = Jx.IntOf(usage, "output_tokens")
                    End If
            End Select
        End Function

        ''' <summary>Answers a permission prompt (can_use_tool) or an AskUserQuestion right in the terminal.</summary>
        Private Async Function HandleClaudeControlAsync(doc As JsonElement, run As ClaudeCode.Run, show As Boolean, ct As CancellationToken) As Task
            Dim requestId = Jx.StrOf(doc, "request_id")
            Dim req = Jx.Prop(doc, "request")
            If Jx.StrOf(req, "subtype") <> "can_use_tool" Then
                ' Unknown control request: allow so Claude can proceed.
                Await _claude.RespondAsync(run, requestId, New JsonObject From {{"behavior", "allow"}}).ConfigureAwait(False)
                Return
            End If
            Dim toolName = Jx.StrOf(req, "tool_name")
            Dim input = Jx.Prop(req, "input")

            If show Then Out.StopSpinner()

            If toolName = "AskUserQuestion" Then
                Dim answered = AskClaudeQuestions(input)
                Await _claude.RespondAsync(run, requestId, New JsonObject From {{"behavior", "allow"}, {"updatedInput", answered}}).ConfigureAwait(False)
                If show Then Out.StartSpinner("Claude Code…")
                Return
            End If

            ' Permission modes that never ask.
            If State.Mode = PermissionMode.BypassPermissions OrElse Not State.Interactive Then
                Await _claude.RespondAsync(run, requestId, AllowResponse(input)).ConfigureAwait(False)
                If show Then Out.StartSpinner("Claude Code…")
                Return
            End If

            Dim summary = ClaudeToolSummary(input)
            Dim i = Menu.Select($" Claude Code wants to use {Ansi.Bold(toolName)}" & If(summary.Length > 0, Ansi.Muted("  " & ToolKit.OneLine(summary, 80)), ""),
                                {New MenuOption("Yes, allow once"),
                                 New MenuOption($"Yes, allow {toolName} for the rest of this session"),
                                 New MenuOption("No, and tell Claude what to do instead")})
            Select Case i
                Case 0
                    Await _claude.RespondAsync(run, requestId, AllowResponse(input)).ConfigureAwait(False)
                Case 1
                    State.Settings.AddToList("session", "claude.allowedTools", toolName)
                    Await _claude.RespondAsync(run, requestId, AllowResponse(input)).ConfigureAwait(False)
                Case Else
                    Dim feedback = Menu.Ask(Ansi.Accent(Ansi.Pointer) & " What should Claude do instead? (Enter to skip)")
                    Await _claude.RespondAsync(run, requestId, New JsonObject From {
                        {"behavior", "deny"}, {"message", If(String.IsNullOrWhiteSpace(feedback), "The user declined this action.", feedback)}}).ConfigureAwait(False)
            End Select
            If show Then Out.StartSpinner("Claude Code…")
        End Function

        Private Shared Function AllowResponse(input As JsonElement) As JsonObject
            Dim o As New JsonObject From {{"behavior", "allow"}}
            If input.ValueKind = JsonValueKind.Object Then o("updatedInput") = JsonNode.Parse(input.GetRawText())
            Return o
        End Function

        ''' <summary>Shows each AskUserQuestion question as a menu and returns the input with the chosen answers filled in.</summary>
        Private Function AskClaudeQuestions(input As JsonElement) As JsonObject
            Dim result As JsonObject = If(input.ValueKind = JsonValueKind.Object, CType(JsonNode.Parse(input.GetRawText()), JsonObject), New JsonObject())
            Dim questions = Jx.ArrOf(input, "questions")
            Dim answers As New JsonArray()
            For Each q In questions
                Dim header = Jx.StrOf(q, "header", "Question")
                Dim text = Jx.StrOf(q, "question", header)
                Dim multi = Jx.BoolOf(q, "multiSelect")
                Dim opts = Jx.ArrOf(q, "options")
                Dim items = opts.Select(Function(op) New MenuOption(Jx.StrOf(op, "label"), ToolKit.OneLine(Jx.StrOf(op, "description"), 70))).ToList()
                items.Add(New MenuOption("Other…", "Type your own answer"))
                Dim chosen As New List(Of String)
                If multi Then
                    Out.Line(Ansi.Muted(" " & text & " (choose one or more; pick Other to finish)"))
                End If
                Do
                    Dim pick = Menu.Select(" " & text, items)
                    If pick < 0 Then Exit Do
                    If pick = items.Count - 1 Then
                        Dim own = Menu.Ask(Ansi.Accent(Ansi.Pointer) & " Your answer:")
                        If Not String.IsNullOrWhiteSpace(own) Then chosen.Add(own)
                        Exit Do
                    End If
                    chosen.Add(Jx.StrOf(opts(pick), "label"))
                    If Not multi Then Exit Do
                Loop
                answers.Add(New JsonObject From {{"header", header}, {"answer", String.Join(", ", chosen)}})
            Next
            result("_answers") = answers
            Return result
        End Function

        Private Shared Function ClaudeToolSummary(input As JsonElement) As String
            If input.ValueKind <> JsonValueKind.Object Then Return ""
            For Each key In {"command", "file_path", "path", "pattern", "url", "prompt", "description", "query"}
                Dim v = Jx.StrOf(input, key, Nothing)
                If Not String.IsNullOrWhiteSpace(v) Then Return v
            Next
            Return ""
        End Function

        ' ------------------------------------------------------------------ the /claude command

        Private Async Function ClaudeCommandAsync(arg As String) As Task
            arg = If(arg, "").Trim()
            Dim parts = arg.Split({" "c}, 2, StringSplitOptions.RemoveEmptyEntries)
            Dim verb = If(parts.Length > 0, parts(0).ToLowerInvariant(), "")
            Dim rest = If(parts.Length > 1, parts(1).Trim(), "")

            Select Case verb
                Case ""
                    Await ShowClaudeStatusAsync().ConfigureAwait(False)
                Case "on", "use"
                    If Not ClaudeAvailable() Then
                        Out.Fail("Claude Code is not installed on this computer. Install it, then try again (https://claude.com/claude-code).")
                        Return
                    End If
                    _model = ClaudeModelId
                    Out.Success("Model: Claude Code (local). Messages now run the claude CLI; it keeps its own session.")
                    If rest.Length > 0 Then Await SendAsync(rest).ConfigureAwait(False)
                Case "off"
                    If IsClaudeModel(_model) Then _model = PickModel(State.Settings.Str("model"))
                    _claudeSession = Nothing
                    Out.Success("Back to the server model: " & _model)
                Case "new", "reset"
                    _claudeSession = Nothing
                    Out.Success("Claude Code will start a fresh session on the next message.")
                Case "model"
                    SetClaudeSetting("claude.model", rest, "Claude model (e.g. sonnet, opus, haiku, or a full id; ""default"" uses Claude's own default)")
                Case "permission", "permission-mode", "mode"
                    SetClaudeSetting("claude.permissionMode", rest, "default | acceptEdits | plan | bypassPermissions")
                Case "cwd", "dir"
                    SetClaudeSetting("claude.cwd", If(rest.Length > 0, Paths.Full(rest), ""), "working directory for Claude Code")
                Case "fallback"
                    SetClaudeSetting("claude.fallbackModel", rest, "model to fall back to when the main one is overloaded")
                Case Else
                    Out.Fail("Usage: /claude [on|off|new|model <m>|permission <mode>|cwd <path>|fallback <m>]")
            End Select
        End Function

        Private Sub SetClaudeSetting(key As String, value As String, what As String)
            If String.IsNullOrWhiteSpace(value) Then
                State.Settings.SetValue("user", key, Nothing)
                Out.Success($"Cleared {key} ({what}).")
            Else
                State.Settings.SetValue("user", key, JsonValue.Create(value))
                Out.Success($"Set {key} = {value}")
            End If
        End Sub

        Private Async Function ShowClaudeStatusAsync() As Task
            Dim st = Await ClaudeCode.StatusAsync().ConfigureAwait(False)
            If Not st.Installed Then
                Out.Line(Ansi.Muted("Claude Code is not installed on this computer."))
                Out.Note("Install it to use Claude modes in chat: https://claude.com/claude-code")
                Return
            End If
            Out.Line(Ansi.Bold("Claude Code") & Ansi.Muted($"  {st.Version}  ·  {st.Source}"))
            Out.Line(Ansi.Muted("  " & st.Path))
            Dim cfg = State.Settings
            Out.Line($"  Model:       {cfg.Str("claude.model", "default")}")
            Out.Line($"  Permission:  {cfg.Str("claude.permissionMode", ClaudeModeFor(State.Mode))}")
            Dim fb = cfg.Str("claude.fallbackModel")
            If Not String.IsNullOrWhiteSpace(fb) Then Out.Line($"  Fallback:    {fb}")
            Out.Line($"  Active:      " & If(IsClaudeModel(_model), Ansi.Ok("yes — this chat uses Claude Code"), Ansi.Muted("no (select it with /claude on or /model)")))
            Out.Note("  /claude on to chat through Claude Code; /claude model/permission/cwd/fallback to configure.")
        End Function

    End Class

End Namespace
