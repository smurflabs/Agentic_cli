Option Strict On
Option Explicit On
Option Infer On

Imports System.IO
Imports System.Text
Imports System.Text.Json
Imports System.Text.Json.Nodes
Imports System.Text.RegularExpressions
Imports System.Threading
Imports AgenticCli.Api
Imports AgenticCli.Core
Imports AgenticCli.Tools
Imports AgenticCli.Ui

Namespace Agent

    ''' <summary>
    ''' A CLI session: connects to the server, keeps the conversation, the model and settings, runs the message box and
    ''' slash commands, and sends messages through the turn runner (with subagents, plan approval and questions).
    ''' </summary>
    Partial Friend NotInheritable Class Session
        Implements IDisposable

        Public ReadOnly Property State As New AgentState()
        Private ReadOnly _opts As CliOptions
        Private _creds As Credentials
        Private _api As ApiClient
        Private _chat As ChatApi
        Private _perm As PermissionEngine
        Private _runner As TurnRunner
        Private _meta As New ChatMeta()
        Private _model As String
        Private ReadOnly _server As New ServerSettings()
        Private _editor As LineEditor
        Private _gitSnapshot As String
        Private _hookContext As String
        Private _outputStyle As String = "default"
        Private _title As String
        Private _ctx As ContextInfo
        Private _lastAnswer As String
        Private _quit As Boolean
        ''' <summary>How often the server checks an answer's progress (tool rounds): it goes on while it gets things done.</summary>
        Private _maxTurns As Integer = 80
        ''' <summary>--max-turns was given: a stop at that many tool rounds, not only a progress check.</summary>
        Private _maxTurnsHard As Boolean
        Private _includeClaude As Boolean = True
        Private _printMode As Boolean
        Private _runCts As CancellationTokenSource
        ''' <summary>The live stream: this CLI as a device of the account, so the web app and this terminal stay in sync.</summary>
        Private _link As LiveLink
        Private _mcpReady As Task = Task.CompletedTask
        Private _gitReady As Task = Task.CompletedTask
        Private ReadOnly _pendingContext As New List(Of String)
        Private ReadOnly _queue As New Queue(Of String)
        Private ReadOnly _typeahead As New StringBuilder()
        Private _fileIndex As List(Of String)
        Private _fileIndexAt As DateTime
        Private _statusLineCache As (Text As String, At As DateTime)
        ' Usage in this session.
        Private _promptTokens As Long
        Private _completionTokens As Long
        Private _turns As Integer
        Private _toolUses As Integer
        Private _apiMs As Long
        ''' <summary>What this session's answers cost the account (micro-dollars), for --max-budget-usd.</summary>
        Private _costMicros As Long

        ''' <summary>--max-budget-usd was reached: no more messages are sent.</summary>
        Public ReadOnly Property BudgetExceeded As Boolean
            Get
                Return _opts.MaxBudgetUsd.HasValue AndAlso _costMicros >= _opts.MaxBudgetUsd.Value * 1000000D
            End Get
        End Property

        ''' <summary>An answer that failed because its model was overloaded or unavailable, with nothing written, and a fallback model to try.</summary>
        Private Function ShouldFallBack(r As TurnResult) As Boolean
            Return r IsNot Nothing AndAlso Not r.Cancelled AndAlso r.Error IsNot Nothing AndAlso r.Text.Length = 0 AndAlso
                   Not String.IsNullOrWhiteSpace(_opts.FallbackModel) AndAlso Not _opts.FallbackModel.Equals(_model, StringComparison.OrdinalIgnoreCase) AndAlso
                   IsUnavailableError(r.Error)
        End Function

        ''' <summary>A model error that another model could answer around: overloaded, rate limited, a 5xx, or the model not available.</summary>
        Friend Shared Function IsUnavailableError(message As String) As Boolean
            If String.IsNullOrWhiteSpace(message) Then Return False
            Return Regex.IsMatch(message, "overload|rate.?limit|\b(429|500|502|503|504|524|529)\b|unavailable|no available channel|model not found|capacity|timed? ?out", RegexOptions.IgnoreCase)
        End Function

        Private Async Function TryRewindAsync(messageId As String) As Task
            Try
                Await _chat.RewindAsync(State.ChatId, messageId, CancellationToken.None).ConfigureAwait(False)
            Catch ex As ApiException
            End Try
        End Function
        ''' <summary>The last answer's thinking phases (/thoughts) and its average speed (shown in the footer).</summary>
        Private _lastThoughts As New List(Of String)
        Private _lastTps As Double
        Private ReadOnly _startedAt As DateTime = DateTime.UtcNow

        Public Sub New(opts As CliOptions, creds As Credentials)
            _opts = opts
            _creds = creds
        End Sub

        Public ReadOnly Property Model As String
            Get
                Return _model
            End Get
        End Property

        Public ReadOnly Property Meta As ChatMeta
            Get
                Return _meta
            End Get
        End Property

        ' ------------------------------------------------------------------ start-up

        ''' <summary>Loads settings, connects to the server and MCP servers, and resumes a conversation when asked.</summary>
        Public Async Function InitAsync(printMode As Boolean) As Task(Of Boolean)
            _printMode = printMode
            Dim projectDir = Environment.CurrentDirectory
            Dim cfg = Settings.Load(projectDir, _opts.SettingsFile)
            _includeClaude = cfg.Bool("includeClaudeConfig", True)
            State.ProjectDir = projectDir
            State.Cwd = projectDir
            State.Settings = cfg
            ' Keys changed with /keys (user settings; a project's settings may add their own).
            KeyBindings.Load(cfg)
            State.Project = ProjectInfo.Detect(projectDir)
            State.Skills = New Skills.SkillStore(projectDir, _includeClaude) With {.Disabled = _opts.DisableSlashCommands}
            State.Shells = New ShellManager(If(_opts.Shell, cfg.Str("shell", "auto")))
            For Each kv In cfg.EnvVars()
                State.Shells.Env(kv.Key) = kv.Value
            Next
            State.Monitors = New MonitorManager(State.Shells)
            State.Hooks = New HookRunner(State) With {.Enabled = Not cfg.Bool("disableAllHooks", False)}
            State.Mcp = New Mcp.McpHost()
            State.Interactive = Not printMode AndAlso Ansi.Interactive
            ' /ide: in VS Code's terminal, the VS Code whose workspace holds this folder connects by itself (ide.autoConnect).
            If State.Interactive AndAlso cfg.Bool("ide.autoConnect", True) AndAlso String.Equals(Environment.GetEnvironmentVariable("TERM_PROGRAM"), "vscode", StringComparison.OrdinalIgnoreCase) Then
                Dim match = IdeLink.Available(projectDir).FirstOrDefault(Function(i) i.MatchedFolder IsNot Nothing)
                If match IsNot Nothing Then State.Ide = New IdeLink(match)
            End If
            Dim mode = AgentState.ParseMode(cfg.Str("permissions.defaultMode", "default"), PermissionMode.Default)
            If _opts.PermissionMode IsNot Nothing Then mode = AgentState.ParseMode(_opts.PermissionMode, mode)
            If _opts.SkipPermissions Then mode = PermissionMode.BypassPermissions
            State.Mode = mode
            For Each d In _opts.AddDirs
                Dim full = Paths.Full(d)
                If Directory.Exists(full) Then State.ExtraDirs.Add(full)
            Next
            State.Mcp.Roots = State.WorkingDirs()
            _perm = New PermissionEngine(State)
            For Each r In _opts.AllowedTools
                _perm.AllowForSession(PermissionRule.Parse(r, "flag"))
            Next
            For Each r In _opts.DisallowedTools
                _perm.SessionDeny.Add(PermissionRule.Parse(r, "flag"))
            Next
            State.Verbose = _opts.Verbose OrElse cfg.Bool("verbose", False)
            State.ShowThinking = State.Verbose OrElse cfg.Bool("showThinking", False)
            _outputStyle = cfg.Str("outputStyle", "default")
            _maxTurns = If(_opts.MaxTurns > 0, _opts.MaxTurns, cfg.Int("maxTurns", 80))
            _maxTurnsHard = _opts.MaxTurns > 0

            _api = New ApiClient(_creds.ServerUrl, _creds.ApiKey)
            _chat = New ChatApi(_api)
            Try
                _meta = Await _chat.MetaAsync(CancellationToken.None).ConfigureAwait(False)
            Catch ex As ApiException
                Out.Fail(If(ex.IsAuth, "The server refused the API key. Run ""agentic login"" to set a new one.", "Cannot use the server: " & ex.Message))
                Return False
            End Try
            If Not _meta.CanTools Then Out.Warning("Your account cannot use chat tools (permission ""Chat tools & code""): server tools are off; tools on this computer still work.")

            _model = PickModel(If(_opts.Model, cfg.Str("model")))
            _server.WebSearch = _meta.WebSearch AndAlso If(_opts.Web, cfg.Bool("webSearch", False))
            _server.CodeInterpreter = _meta.CodeInterpreter AndAlso cfg.Bool("codeInterpreter", False)
            _server.McpServers = cfg.Strings("serverMcpServers").Where(Function(id) _meta.Servers.Any(Function(s) s.Id = id)).ToList()
            _server.ReasoningEffort = NormalizeEffort(If(_opts.Effort, cfg.Str("effort", "medium")))
            ' Caveman mode stays on across sessions until turned off (/cavemode off).
            Dim cave = cfg.Str("cavemode", Nothing)
            _server.Caveman = If(cave = "full" OrElse cave = "ultra" OrElse cave = "wenyan", cave, Nothing)
            _server.PermissionMode = "auto"

            _runner = New TurnRunner(_chat, State, _perm) With {
                .RunSubagent = AddressOf RunSubagentAsync,
                .ApprovePlan = If(State.Interactive, New Func(Of String, (Choice As String, Feedback As String))(AddressOf ApprovePlanInteractive), Nothing),
                .Interrupt = Sub() _runCts?.Cancel(),
                .Parallel = cfg.Bool("parallelAgents", True),
                .MaxParallelAgents = Math.Clamp(cfg.Int("maxParallelAgents", 6), 1, 16),
                .MaxParallelReads = Math.Clamp(cfg.Int("maxParallelReads", 8), 1, 32)}
            ' The server tells the model to split work only when Parallel is on (and runs a round's subagents together).
            _server.Parallel = If(_runner.Parallel, CType(Nothing, Boolean?), False)
            _server.MaxParallelAgents = If(cfg.Int("maxParallelAgents", 0) > 0, CType(_runner.MaxParallelAgents, Integer?), Nothing)
            ' Research notes: on in interactive sessions; a headless run (-p, scripts, CI) writes no notes file unless researchNotes is set.
            _server.ResearchNotes = If(cfg.Bool("researchNotes", State.Interactive), CType(Nothing, Boolean?), False)

            ' MCP servers and the git snapshot load in the background; the first message waits for them.
            _mcpReady = ConnectMcpAsync()
            ' Interactive sessions join the live stream: messages sent to this chat from the web app show (and use this CLI's
            ' tools) here, and questions and approvals answered there close here.
            If State.Interactive AndAlso Not printMode AndAlso Not cfg.Bool("disableWebSync", False) Then
                Dim folder = Path.GetFileName(projectDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                _link = New LiveLink(_api, LiveLink.MakeDeviceId(projectDir), $"CLI · {If(String.IsNullOrEmpty(folder), projectDir, folder)} ({Environment.MachineName})",
                                     _runner.ClientId, AddressOf CatalogJson)
                _runner.Link = _link
                _link.Start()
                Dim unusedCatalog = _mcpReady.ContinueWith(Sub(t) _link.PushCatalog())
            End If
            _gitReady = Task.Run(Sub() _gitSnapshot = State.Project.GitSnapshot())

            ' The matcher of a SessionStart hook is the source (startup, resume, clear, compact), like Claude Code's.
            Dim source = If(_opts.ContinueLast OrElse _opts.ResumePick, "resume", "startup")
            Dim h = State.Hooks.Run("SessionStart", source, New Dictionary(Of String, Object) From {{"source", source}})
            _hookContext = h.Context

            If _opts.ContinueLast Then
                Dim latest = Sessions.List(projectDir).FirstOrDefault()
                If latest Is Nothing Then
                    If Not printMode Then Out.Note("No earlier conversation in this folder; starting a new one.")
                Else
                    Await OpenConversationAsync(latest.ChatId, Not printMode).ConfigureAwait(False)
                End If
            ElseIf _opts.ResumePick Then
                If Not String.IsNullOrWhiteSpace(_opts.ResumeId) Then
                    Await OpenConversationAsync(_opts.ResumeId, Not printMode).ConfigureAwait(False)
                ElseIf State.Interactive Then
                    Await ResumePickerAsync().ConfigureAwait(False)
                End If
            End If
            ' --fork-session: continue in a copy of the resumed conversation; the original stays as it was.
            If _opts.ForkSession AndAlso Not String.IsNullOrEmpty(State.ChatId) Then
                Dim original = State.ChatId
                Try
                    Dim b = Await _chat.ForkAsync(original, _opts.Name, CancellationToken.None).ConfigureAwait(False)
                    Await OpenConversationAsync(b.Id, False).ConfigureAwait(False)
                    If Not printMode Then Out.Note($"Continuing in a copy (""{b.Title}""); the original conversation stays as it was.")
                Catch ex As ApiException
                    Out.Warning("Could not fork the conversation (" & ex.Message & "); continuing in the original.")
                End Try
            End If
            Return True
        End Function

        Private Function PickModel(wanted As String) As String
            If IsClaudeModel(wanted) AndAlso ClaudeAvailable() Then Return ClaudeModelId
            If Not String.IsNullOrWhiteSpace(wanted) Then
                Dim m = _meta.Find(wanted)
                If m IsNot Nothing Then Return m.Id
                If Not _printMode Then Out.Warning($"Model '{wanted}' is not offered by the server; using the default.")
            End If
            ' The model last used on this account (web app, desktop app or CLI), then the server's default.
            If Not String.IsNullOrWhiteSpace(_meta.LastModel) AndAlso _meta.Find(_meta.LastModel) IsNot Nothing Then Return _meta.Find(_meta.LastModel).Id
            If Not String.IsNullOrWhiteSpace(_meta.DefaultModel) Then Return _meta.DefaultModel
            Return _meta.AllModels().Select(Function(m) m.Id).FirstOrDefault()
        End Function

        ' ------------------------------------------------------------------ control by a host program (stream-json input)

        Private ReadOnly _hostTools As New List(Of ITool)
        Public Property HostToolInvoker As Func(Of String, JsonElement, CancellationToken, Task(Of ToolResult))

        ''' <summary>Tools the host program provides: [{name, description, parameters, readOnly}]. They run without asking.</summary>
        Public Function SetHostTools(defs As List(Of JsonElement)) As List(Of String)
            SyncLock _hostTools
                _hostTools.Clear()
                For Each d In defs
                    Dim fn = If(Jx.Prop(d, "function").ValueKind = JsonValueKind.Object, Jx.Prop(d, "function"), d)
                    Dim name = New String(Jx.StrOf(fn, "name").Where(Function(ch) Char.IsLetterOrDigit(ch) OrElse ch = "_"c OrElse ch = "-"c).ToArray())
                    If name.Length = 0 OrElse name.Length > 64 Then Continue For
                    Dim schema = If(Jx.Prop(fn, "parameters").ValueKind = JsonValueKind.Object, Jx.ParseObject(Jx.Prop(fn, "parameters").GetRawText()), Nothing)
                    _hostTools.Add(New HostTool(name, Jx.StrOf(fn, "description"), schema, Jx.BoolOf(fn, "readOnly"), Function(n, input, ct) HostToolInvoker(n, input, ct)))
                    _perm.AllowForSession(PermissionRule.Parse(name, "host"))
                Next
                Return _hostTools.Select(Function(t) t.Name).ToList()
            End SyncLock
        End Function

        ''' <summary>Stops the answer being written (Esc, or the host's interrupt).</summary>
        Public Sub Interrupt()
            _runCts?.Cancel()
        End Sub

        Public Function TrySetModel(id As String) As Boolean
            If IsClaudeModel(id) AndAlso ClaudeAvailable() Then
                _model = ClaudeModelId
                Return True
            End If
            Dim m = _meta.Find(id)
            If m Is Nothing Then Return False
            _model = m.Id
            Dim unused = _chat.SetLastModelAsync(_model)
            Return True
        End Function

        Public Sub SetEffortLevel(level As String)
            _server.ReasoningEffort = NormalizeEffort(level)
        End Sub

        ''' <summary>The next message starts a new conversation.</summary>
        Public Sub ResetConversation()
            State.ChatId = Nothing
            State.Checkpoints = Nothing
            State.Todos = New List(Of TodoItem)
            State.ReadFiles.Clear()
            _title = Nothing
            _ctx = Nothing
        End Sub

        Public Shared Function NormalizeEffort(s As String) As String
            Select Case If(s, "").Trim().ToLowerInvariant()
                Case "none", "off", "0" : Return "none"
                Case "low", "min", "minimal" : Return "low"
                Case "high", "max", "think hard" : Return "high"
                Case "ultracode", "ultra", "ultrathink", "maximum" : Return "ultracode"
                Case Else : Return "medium"
            End Select
        End Function

        ''' <summary>A server asks the user something while a tool runs (MCP elicitation): asked here in the terminal.</summary>
        Private Function ElicitAsync(server As String, message As String, schema As JsonElement) As Task(Of JsonObject)
            If Not State.Interactive Then Return Task.FromResult(New JsonObject From {{"action", "decline"}})
            Out.StopSpinner()
            Out.Line(Ansi.Bold(server & " asks: ") & message)
            Dim content As New JsonObject()
            Dim props As JsonElement
            If schema.ValueKind = JsonValueKind.Object AndAlso schema.TryGetProperty("properties", props) AndAlso props.ValueKind = JsonValueKind.Object Then
                For Each p In props.EnumerateObject()
                    Dim label = Jx.StrOf(p.Value, "title", Jx.StrOf(p.Value, "description", p.Name))
                    Dim type = Jx.StrOf(p.Value, "type", "string")
                    Dim answer = Menu.Ask(" " & label, secret:=Text.RegularExpressions.Regex.IsMatch(p.Name, "password|secret|token|key", Text.RegularExpressions.RegexOptions.IgnoreCase))
                    If answer Is Nothing Then Return Task.FromResult(New JsonObject From {{"action", "cancel"}})
                    Dim n As Double
                    content(p.Name) = If(type = "boolean", CType(JsonValue.Create(answer.Trim().ToLowerInvariant() = "true" OrElse answer.Trim().ToLowerInvariant() = "yes" OrElse answer.Trim().ToLowerInvariant() = "y"), JsonNode),
                                         If((type = "number" OrElse type = "integer") AndAlso Double.TryParse(answer, Globalization.NumberStyles.Float, Globalization.CultureInfo.InvariantCulture, n), JsonValue.Create(n), JsonValue.Create(answer)))
                Next
            Else
                Dim pick = Menu.Select(" Answer?", {New MenuOption("Yes"), New MenuOption("No")})
                Return Task.FromResult(New JsonObject From {{"action", If(pick = 0, "accept", "decline")}, {"content", New JsonObject()}})
            End If
            Return Task.FromResult(New JsonObject From {{"action", "accept"}, {"content", content}})
        End Function

        Private Async Function ConnectMcpAsync() As Task
            Mcp.McpConnection.Elicit = AddressOf ElicitAsync
            Dim configs = Mcp.McpConfig.Load(State.ProjectDir, _opts.McpConfigs, _includeClaude)
            ' Servers from Claude Code, its plugins or Claude Desktop switched off here (/mcp disable): their files are not touched.
            Dim off = State.Settings.Strings("disabledMcpServers")
            For Each s In configs.Where(Function(x) Not x.Editable AndAlso x.Scope <> "flag" AndAlso off.Contains(x.Id, StringComparer.OrdinalIgnoreCase))
                s.Enabled = False
            Next
            ' --strict-mcp-config: only the servers given on the command line.
            If _opts.StrictMcpConfig Then configs = configs.Where(Function(s) s.Scope = "flag").ToList()
            State.Mcp.Configure(configs)
            If configs.Count = 0 Then Return
            Dim approvedList = State.Settings.Strings("enabledMcpjsonServers")
            Dim rejected = State.Settings.Strings("disabledMcpjsonServers")
            Dim allProject = State.Settings.Bool("enableAllProjectMcpServers", False)
            ' Servers from the project's .mcp.json run programs from the repository: each is approved once.
            Dim pending = configs.Where(Function(s) s.Scope = "project" AndAlso s.Enabled AndAlso Not allProject AndAlso
                                                    Not approvedList.Contains(s.Id, StringComparer.OrdinalIgnoreCase) AndAlso
                                                    Not rejected.Contains(s.Id, StringComparer.OrdinalIgnoreCase)).ToList()
            If pending.Count > 0 AndAlso State.Interactive Then
                For Each s In pending
                    Dim what = If(s.Transport = Mcp.McpTransportKind.Stdio, s.Command & " " & String.Join(" ", s.Args), s.Endpoint)
                    Dim pick = Menu.Select($" This project's .mcp.json wants to start the MCP server {Ansi.Bold(s.Id)}:" & vbLf & "   " & Ansi.Muted(ToolKit.OneLine(what, 150)) & vbLf & " Use it?",
                                           {New MenuOption("Yes, use this server"), New MenuOption("Yes, use all servers in this project's .mcp.json"), New MenuOption("No")})
                    Select Case pick
                        Case 0 : State.Settings.AddToList("local", "enabledMcpjsonServers", s.Id)
                        Case 1
                            State.Settings.SetValue("local", "enableAllProjectMcpServers", Text.Json.Nodes.JsonValue.Create(True))
                            allProject = True
                            Exit For
                        Case Else : State.Settings.AddToList("local", "disabledMcpjsonServers", s.Id)
                    End Select
                Next
                approvedList = State.Settings.Strings("enabledMcpjsonServers")
                rejected = State.Settings.Strings("disabledMcpjsonServers")
                allProject = State.Settings.Bool("enableAllProjectMcpServers", False)
            End If
            Dim ok = Function(s As Mcp.McpServerOptions) s.Scope <> "project" OrElse allProject OrElse approvedList.Contains(s.Id, StringComparer.OrdinalIgnoreCase)
            Await Task.Run(Function() State.Mcp.ConnectAllAsync(ok)).ConfigureAwait(False)
        End Function

        Private Async Function EnsureReadyAsync() As Task
            Dim both = Task.WhenAll(_mcpReady, _gitReady)
            If Not both.IsCompleted Then
                Out.StartSpinner("Connecting MCP servers…")
                Await Task.WhenAny(both, Task.Delay(15000)).ConfigureAwait(False)
                Out.StopSpinner()
            End If
        End Function

        ' ------------------------------------------------------------------ conversations

        Private Async Function EnsureChatAsync() As Task(Of Boolean)
            If Not String.IsNullOrEmpty(State.ChatId) Then Return True
            Try
                ' -n/--name titles it; --no-session-persistence makes it a temporary chat (not kept in the history).
                Dim c = Await _chat.CreateAsync(If(String.IsNullOrWhiteSpace(_opts.Name), "New chat", _opts.Name.Trim()), _model, _server, CancellationToken.None,
                                                temporary:=_opts.NoSessionPersistence).ConfigureAwait(False)
                SetChat(c.Id)
                If Not String.IsNullOrWhiteSpace(_opts.Name) Then
                    _title = _opts.Name.Trim()
                    SetTerminalTitle()
                End If
                Return True
            Catch ex As ApiException
                Out.Fail("Could not start a conversation: " & ex.Message)
                Return False
            End Try
        End Function

        Private Sub SetChat(chatId As String)
            State.ChatId = chatId
            State.Checkpoints = New CheckpointStore(Path.Combine(Paths.ProjectDataDir(State.ProjectDir), "checkpoints", chatId))
            _link?.Watch(chatId)
        End Sub

        ''' <summary>What this CLI offers the server as a device: its tools and instructions (for messages sent from the web app).</summary>
        Private Function CatalogJson() As String
            Dim tools As New System.Text.Json.Nodes.JsonArray()
            For Each t In BuildTools()
                tools.Add(New System.Text.Json.Nodes.JsonObject From {{"name", t.Name}, {"description", t.Description}, {"parameters", t.Parameters()}, {"x-risk", If(t.IsReadOnly, "read", "change")}})
            Next
            Return New System.Text.Json.Nodes.JsonObject From {{"name", If(_link?.DeviceName, "CLI")}, {"clientTools", tools}, {"clientInstructions", BuildInstructions()}}.ToJsonString()
        End Function

        ''' <summary>
        ''' A message was sent to this chat from another window (the web app): show it and its answer here as it is written, run
        ''' the tools it calls on this computer, then ask its questions here too. Tool calls for chats not open here just run.
        ''' </summary>
        Private Async Function FollowRemoteAsync() As Task
            If _link Is Nothing Then Return
            Await EnsureReadyAsync().ConfigureAwait(False)
            Dim toolsOnly = String.IsNullOrEmpty(State.ChatId) OrElse Not _link.HasChatWork(AddressOf OnRemoteTitle)
            Dim o As New TurnOptions With {.ChatId = State.ChatId, .Follow = True, .ToolsOnly = toolsOnly, .Model = _model, .Server = _server,
                                           .Tools = BuildTools(), .Instructions = BuildInstructions(), .MaxToolRounds = _maxTurns, .MaxToolRoundsHard = _maxTurnsHard, .Render = True}
            If Not toolsOnly Then
                State.Turn = State.Checkpoints.NextTurn
                State.Checkpoints.BeginTurn(State.Turn, "(message from another window)")
            End If
            _runCts = New CancellationTokenSource()
            Dim stopKeys = WatchKeys(_runCts)
            Dim r As TurnResult = Nothing
            Dim failure As Exception = Nothing
            Try
                r = Await _runner.RunAsync(o, _runCts.Token).ConfigureAwait(False)
            Catch ex As Exception
                failure = ex
            End Try
            Await stopKeys().ConfigureAwait(False)
            If Not toolsOnly Then _link.FollowEnded()
            If failure IsNot Nothing Then Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw()
            If toolsOnly Then Return
            Await FinishTurnAsync(r, If(r.AutoGoal, "(goal: automatic continuation)", If(r.Prompt, "(message from another window)")), Nothing, 0).ConfigureAwait(False)
        End Function

        Private Sub OnRemoteTitle(title As String)
            If String.IsNullOrWhiteSpace(title) Then Return
            _title = title
            SetTerminalTitle()
        End Sub

        ''' <summary>Opens an earlier conversation and shows its last messages.</summary>
        Public Async Function OpenConversationAsync(chatId As String, show As Boolean) As Task(Of Boolean)
            Dim c As Conversation
            Try
                c = Await _chat.GetAsync(chatId, CancellationToken.None).ConfigureAwait(False)
            Catch ex As ApiException
                Out.Fail("Could not open the conversation: " & ex.Message)
                Return False
            End Try
            SetChat(c.Id)
            _title = c.Title
            If String.IsNullOrWhiteSpace(_opts.Model) AndAlso Not String.IsNullOrWhiteSpace(c.Model) AndAlso _meta.Find(c.Model) IsNot Nothing Then _model = c.Model
            If show Then ShowTranscript(c, 12)
            SetTerminalTitle()
            Return True
        End Function

        Private Sub ShowTranscript(c As Conversation, last As Integer)
            Out.Line(Ansi.Muted($"Resumed ""{c.Title}"" ({c.Messages.Count} messages)"))
            Dim msgs = c.Messages.Skip(Math.Max(0, c.Messages.Count - last)).ToList()
            If msgs.Count < c.Messages.Count Then Out.Line(Ansi.Dim2($"  … {c.Messages.Count - msgs.Count} earlier messages"))
            For Each m In msgs
                If m.Role = "user" Then
                    EchoUser(m.Content)
                Else
                    For Each t In m.ToolCalls.Take(12)
                        Out.Line(Ansi.Ok(Ansi.Dot) & " " & Ansi.Bold(Jx.StrOf(t, "name")) & Ansi.Muted("(" & ToolKit.OneLine(Jx.StrOf(t, "arguments"), 70) & ")"))
                    Next
                    Dim text = If(m.Content, "")
                    Dim lines = text.Split(ChrW(10))
                    If lines.Length > 30 Then text = String.Join(vbLf, lines.Take(30)) & vbLf & $"… ({lines.Length - 30} more lines)"
                    If text.Trim().Length > 0 Then Out.Write(MarkdownStream.Render(text, Ansi.Text(Ansi.Dot) & " ", "  "))
                End If
            Next
            Out.Line()
        End Sub

        Friend Shared Sub EchoUser(text As String)
            Dim lines = If(text, "").Replace(ChrW(13), "").Split(ChrW(10))
            Dim w = Ansi.TermWidth() - 3
            Dim sb As New StringBuilder()
            For i = 0 To Math.Min(lines.Length, 40) - 1
                For Each wrapped In Ansi.WrapText(lines(i), w, If(i = 0, "> ", "  "), "  ")
                    sb.Append(Ansi.Muted(wrapped)).Append(Environment.NewLine)
                Next
            Next
            If lines.Length > 40 Then sb.Append(Ansi.Muted($"  … +{lines.Length - 40} lines")).Append(Environment.NewLine)
            Out.Write(sb.ToString())
        End Sub

        Private Sub SetTerminalTitle()
            If State.Interactive Then Out.Raw(Ansi.Title(Ansi.Star & " " & If(String.IsNullOrWhiteSpace(_title) OrElse _title = "New chat", "Agentic", _title)))
        End Sub

        ' ------------------------------------------------------------------ tools and instructions

        ''' <param name="depth">For a subagent: how deep it is (1 = started by the main conversation); below TaskTool.MaxDepth it may start its own.</param>
        Public Function BuildTools(Optional def As SubagentDef = Nothing, Optional depth As Integer = 0) As List(Of ITool)
            Dim list As New List(Of ITool) From {
                New ReadTool(), New EditTool(), New MultiEditTool(), New WriteTool(), New GlobTool(), New GrepTool(), New LsTool(),
                New BashTool(State.Shells), New BashOutputTool(State.Shells), New KillShellTool(State.Shells), New WebFetchTool(),
                New TodoWriteTool(), New NotebookEditTool()}
            If State.Skills.Skills().Count > 0 Then
                list.Add(New SkillTool())
                list.Add(New SkillFileTool())
            End If
            ' /ide: the problems the connected editor reports.
            If State.Ide IsNot Nothing Then list.Add(New IdeDiagnosticsTool())
            If def Is Nothing Then
                SyncLock _hostTools
                    list.AddRange(_hostTools)
                End SyncLock
                list.Add(New TaskTool(Function() AgentListText()))
                ' Monitor: its events arrive while the session waits, so only in interactive sessions.
                If State.Interactive Then list.Add(New MonitorTool())
                If State.Mode = PermissionMode.Plan Then list.Add(New ExitPlanModeTool())
            ElseIf depth < TaskTool.MaxDepth Then
                ' A subagent may start subagents of its own, a few levels deep.
                list.Add(New TaskTool(Function() AgentListText()))
            End If
            For Each c In State.Mcp.ConnectedServers()
                For Each t In c.Status.Tools
                    list.Add(New McpTool(c.Options.Id, t, State.Mcp))
                Next
            Next
            ' Servers that offer resources: ListMcpResources and ReadMcpResource.
            If State.Mcp.ConnectedServers().Any(Function(c) c.HasCapability("resources")) Then
                list.Add(New McpResourceTool(State.Mcp, False))
                list.Add(New McpResourceTool(State.Mcp, True))
            End If
            ' Tools named in deny rules without a pattern are not offered at all.
            Dim denied = _perm.Rules("deny").Where(Function(r) r.Spec Is Nothing).Select(Function(r) r.Tool).ToList()
            list.RemoveAll(Function(t) denied.Any(Function(d) d.Equals(t.Name, StringComparison.OrdinalIgnoreCase) OrElse
                                                                  (d.StartsWith("mcp__") AndAlso t.Name.StartsWith(d.TrimEnd("*"c).TrimEnd("_"c) & "__", StringComparison.OrdinalIgnoreCase))))
            If def Is Nothing AndAlso State.Mode = PermissionMode.Plan Then list.RemoveAll(Function(t) Not t.IsReadOnly)
            ' --tools: only the named built-in tools (MCP servers' tools and the host's are not built in).
            If _opts.Tools IsNot Nothing Then
                list.RemoveAll(Function(t) TypeOf t IsNot McpTool AndAlso TypeOf t IsNot HostTool AndAlso
                                           Not _opts.Tools.Any(Function(n) n.Equals(t.Name, StringComparison.OrdinalIgnoreCase)))
            End If
            If def IsNot Nothing AndAlso def.Tools IsNot Nothing Then
                list.RemoveAll(Function(t) Not def.Tools.Any(Function(n) n.Equals(t.Name, StringComparison.OrdinalIgnoreCase) OrElse
                                                                      (n.StartsWith("mcp__") AndAlso t.Name.StartsWith(n.TrimEnd("*"c), StringComparison.OrdinalIgnoreCase))))
            End If
            Return list.Take(90).ToList()
        End Function

        Private Function AgentListText() As String
            Return String.Join(vbLf, Subagents.All(State.ProjectDir, _includeClaude).Select(Function(a) $"- {a.Name}: {a.Description}" &
                If(a.Tools Is Nothing, " (Tools: all)", " (Tools: " & String.Join(", ", a.Tools) & ")")))
        End Function

        Public Function BuildInstructions(Optional def As SubagentDef = Nothing) As String
            Return Instructions.Build(State, New InstructionOptions With {
                .Model = _model, .OutputStyle = _outputStyle, .AppendSystemPrompt = If(def Is Nothing, _opts.AppendSystemPrompt, Nothing),
                .SystemPrompt = If(def Is Nothing, _opts.SystemPrompt, Nothing), .HookContext = If(def Is Nothing, _hookContext, Nothing),
                .GitSnapshot = If(def Is Nothing, _gitSnapshot, Nothing), .ShellDescription = State.Shells.Describe(), .Subagent = def,
                .WebSearch = _server.WebSearch, .IncludeClaude = _includeClaude,
                .Parallel = _runner Is Nothing OrElse _runner.Parallel, .MaxParallelAgents = If(_runner Is Nothing, 6, _runner.MaxParallelAgents)})
        End Function

        ' ------------------------------------------------------------------ sending

        ''' <summary>@path mentions: text files are attached as context (and count as read), images are uploaded for the model to see.</summary>
        Private Async Function PrepareMentionsAsync(prompt As String) As Task(Of (Content As String, Attachments As List(Of String)))
            Dim attachments As New List(Of String)
            Await AttachPastedImagesAsync(prompt, attachments).ConfigureAwait(False)
            Dim blocks As New StringBuilder()
            Dim seen As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
            For Each m As Match In Regex.Matches(prompt, "(?<=^|\s)@(""[^""]+""|[^\s,;]+)")
                Dim raw = m.Groups(1).Value.Trim(""""c).TrimEnd("."c, ")"c, "?"c, "!"c)
                Dim full As String
                Try
                    full = State.Resolve(raw)
                Catch ex As Exception When TypeOf ex Is ArgumentException OrElse TypeOf ex Is NotSupportedException OrElse TypeOf ex Is PathTooLongException
                    Continue For
                End Try
                If Not seen.Add(full) Then Continue For
                Dim ext = Path.GetExtension(full).ToLowerInvariant()
                If File.Exists(full) AndAlso {".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp"}.Contains(ext) Then
                    Try
                        Dim up = Await _chat.UploadAsync(Path.GetFileName(full), File.ReadAllBytes(full), CancellationToken.None).ConfigureAwait(False)
                        attachments.Add(up.Id)
                        Out.Line(Ansi.Muted($"  {Ansi.Elbow}  Attached image {State.Display(full)}"))
                    Catch ex As Exception When TypeOf ex Is ApiException OrElse TypeOf ex Is IOException
                        Out.Warning($"Could not attach {raw}: {ex.Message}")
                    End Try
                ElseIf File.Exists(full) Then
                    If TextFile.IsBinary(full) Then
                        blocks.AppendLine($"<file path=""{full}"">(binary file, {New FileInfo(full).Length:N0} bytes)</file>")
                        Continue For
                    End If
                    Dim text = TextFile.Load(full).Text
                    Dim lines = text.Split(ChrW(10))
                    Dim shown = lines.Take(2000).Select(Function(l, i) (i + 1).ToString().PadLeft(6) & vbTab & l)
                    blocks.AppendLine($"<file path=""{full}"">")
                    blocks.AppendLine(String.Join(vbLf, shown))
                    If lines.Length > 2000 Then blocks.AppendLine($"(… {lines.Length - 2000} more lines; Read with offset to see them)")
                    blocks.AppendLine("</file>")
                    State.MarkRead(full)
                    Out.Line(Ansi.Muted($"  {Ansi.Elbow}  Read {State.Display(full)} ({Math.Min(lines.Length, 2000)} lines)"))
                ElseIf Directory.Exists(full) Then
                    Dim entries = New FileWalker(full) With {.MaxDepth = 1}.Walk(True, CancellationToken.None).Take(200).Select(Function(e) Path.GetFileName(e.FullPath) & If(e.IsDir, "/", "")).ToList()
                    blocks.AppendLine($"<directory path=""{full}"">")
                    blocks.AppendLine(String.Join(vbLf, entries))
                    blocks.AppendLine("</directory>")
                    Out.Line(Ansi.Muted($"  {Ansi.Elbow}  Listed {State.Display(full)}"))
                End If
            Next
            Dim content = prompt
            If blocks.Length > 0 Then content &= vbLf & vbLf & "Files the user referenced with @:" & vbLf & blocks.ToString()
            Return (content, attachments)
        End Function

        ''' <summary>Sends a message and handles what follows (questions, plan approval, stop hooks); returns the last answer.</summary>
        Public Async Function SendAsync(prompt As String, Optional observer As Action(Of String, Object) = Nothing, Optional stopHookActive As Boolean = False,
                                        Optional depthOfFollowUps As Integer = 0) As Task(Of TurnResult)
            Await EnsureReadyAsync().ConfigureAwait(False)
            ' /research asked for this message only.
            Dim research = _researchNext
            _researchNext = False
            If BudgetExceeded Then
                Out.Fail($"Stopped: this session's model calls cost ${_costMicros / 1000000D:0.####}, the limit is ${_opts.MaxBudgetUsd.Value} (--max-budget-usd).")
                Return Nothing
            End If
            Dim prepared = Await PrepareMentionsAsync(prompt).ConfigureAwait(False)
            Dim content = prepared.Content
            If _pendingContext.Count > 0 Then
                content = String.Join(vbLf, _pendingContext) & vbLf & vbLf & content
                _pendingContext.Clear()
            End If
            ' Claude Code mode: the local claude CLI answers and keeps its own session (no server chat, hooks or checkpoints).
            If IsClaudeModel(_model) Then Return Await RunClaudeAsync(content).ConfigureAwait(False)
            Dim h = State.Hooks.Run("UserPromptSubmit", "", New Dictionary(Of String, Object) From {{"prompt", prompt}})
            If h.Blocked Then
                Out.Fail("Your message was blocked by a hook: " & h.Reason)
                Return Nothing
            End If
            If h.Context IsNot Nothing Then content &= vbLf & vbLf & "<user-prompt-submit-hook>" & vbLf & h.Context & vbLf & "</user-prompt-submit-hook>"
            ' /ide: the lines selected in the editor (or the file open in it) go with the message.
            If State.Ide IsNot Nothing Then
                Dim ideNote = Await State.Ide.ContextAsync(Function(p) State.Display(p), CancellationToken.None).ConfigureAwait(False)
                If ideNote IsNot Nothing Then content &= vbLf & vbLf & ideNote
            End If
            If Not Await EnsureChatAsync().ConfigureAwait(False) Then Return Nothing
            State.Turn = State.Checkpoints.NextTurn
            State.Checkpoints.BeginTurn(State.Turn, prompt)

            Dim o As New TurnOptions With {
                .ChatId = State.ChatId, .Content = content, .Attachments = prepared.Attachments, .Model = _model, .Server = _server,
                .Tools = BuildTools(), .Instructions = BuildInstructions(), .MaxToolRounds = _maxTurns, .MaxToolRoundsHard = _maxTurnsHard, .Render = Not _printMode,
                .Observer = observer, .StopHookActive = stopHookActive, .Research = research}
            _runCts = New CancellationTokenSource()
            Dim stopKeys = WatchKeys(_runCts)
            Dim r As TurnResult = Nothing
            Dim failure As Exception = Nothing
            Try
                r = Await _runner.RunAsync(o, _runCts.Token).ConfigureAwait(False)
                ' --fallback-model: the model was overloaded or unavailable and wrote nothing. The question is taken back (so it
                ' is not in the conversation twice) and the fallback model answers it.
                If ShouldFallBack(r) Then
                    Dim fallback = PickModel(_opts.FallbackModel)
                    If Not _printMode Then Out.Warning($"{_model} is unavailable ({ToolKit.OneLine(r.Error, 120)}); {fallback} answers instead.")
                    If r.UserMessageId IsNot Nothing Then Await TryRewindAsync(r.UserMessageId).ConfigureAwait(False)
                    _model = fallback
                    o.Model = fallback
                    r = Await _runner.RunAsync(o, _runCts.Token).ConfigureAwait(False)
                End If
            Catch ex As Exception
                failure = ex
            End Try
            Await stopKeys().ConfigureAwait(False)
            If failure IsNot Nothing Then Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw()
            Return Await FinishTurnAsync(r, prompt, observer, depthOfFollowUps).ConfigureAwait(False)
        End Function

        ''' <summary>After an answer (sent here or followed from another window): totals, title, errors, then its questions and follow-ups.</summary>
        Private Async Function FinishTurnAsync(r As TurnResult, prompt As String, observer As Action(Of String, Object), depthOfFollowUps As Integer) As Task(Of TurnResult)
            If r.UserMessageId IsNot Nothing Then State.Checkpoints.SetMessageId(State.Turn, r.UserMessageId)
            If r.Thoughts.Count > 0 OrElse r.Text.Length > 0 Then _lastThoughts = r.Thoughts.ToList()
            If r.TokensPerSecond > 0 Then _lastTps = r.TokensPerSecond
            _turns += 1
            _toolUses += r.ToolUses
            _promptTokens += r.PromptTokens
            _completionTokens += r.CompletionTokens
            _apiMs += r.DurationMs
            _costMicros += r.CostMicros
            If r.Error IsNot Nothing AndAlso Not r.Cancelled Then
                State.Hooks.Run("StopFailure", "api_error", New Dictionary(Of String, Object) From {{"error", r.Error}, {"model", _model}})
            End If
            If r.Context IsNot Nothing Then _ctx = r.Context
            ' A goal the server reports met — or stopped because the user asked it to — is cleared, so it is not re-sent.
            If r.GoalStatus = "met" OrElse r.GoalStatus = "stopped" Then _server.Goal = Nothing
            If r.Text.Length > 0 Then
                _lastAnswer = r.Text.ToString()
                _lastMessageId = r.MessageId
                _answerCount += 1
            End If
            If Not String.IsNullOrWhiteSpace(r.Title) Then
                _title = r.Title
                SetTerminalTitle()
            End If
            Sessions.Upsert(State.ProjectDir, New SessionRecord With {.ChatId = State.ChatId, .Title = _title, .Model = _model, .UpdatedUtc = DateTime.UtcNow,
                                                                      .Turns = State.Turn, .LastPrompt = prompt})
            If Not _printMode Then
                If r.Cancelled Then
                    Out.Line(Ansi.Muted("  " & Ansi.Elbow & "  ") & Ansi.Bad("Interrupted") & Ansi.Muted(" · What should Agentic do instead?"))
                ElseIf r.Error IsNot Nothing Then
                    Out.Fail(r.Error)
                End If
                If r.StopReason = "max_rounds" Then Out.Warning(If(_maxTurnsHard, $"Reached the limit of {_maxTurns} tool rounds you set with --max-turns. Say ""continue"" to go on.",
                    "It stopped using tools: its last tool rounds brought nothing new (or the server's tool limit was reached). Say ""continue"" to go on."))
                If r.DurationMs > 30000 AndAlso Not r.Cancelled Then Notify(If(r.Error IsNot Nothing, "The answer stopped with an error", "The answer is ready"))
                Out.Line()
            End If
            If r.Cancelled Then Return r
            ' Messages added while it worked that it did not get to (from the web app): they go next.
            If r.SteersLeft.Count > 0 Then
                SyncLock _queue
                    For Each t In r.SteersLeft
                        _queue.Enqueue(t)
                    Next
                End SyncLock
                If Not _printMode Then Out.Line(Ansi.Muted($"  ↻ {r.SteersLeft.Count} message{If(r.SteersLeft.Count = 1, "", "s")} added while it worked go next"))
            End If
            ' The model asked a question (ask_user): answer it and continue.
            If r.Questions.Count > 0 AndAlso State.Interactive AndAlso depthOfFollowUps < 10 Then
                Dim answer = AskQuestions(r.Questions)
                If answer IsNot Nothing Then
                    EchoUser(answer)
                    Return Await SendAsync(answer, observer, False, depthOfFollowUps + 1).ConfigureAwait(False)
                End If
            End If
            If r.FollowUp IsNot Nothing AndAlso depthOfFollowUps < 10 Then
                If Not _printMode Then Out.Line(Ansi.Muted(If(r.FollowUpFromStopHook, "  ↻ Stop hook: ", "  ↻ ") & ToolKit.OneLine(r.FollowUp, 120)))
                Return Await SendAsync(r.FollowUp, observer, r.FollowUpFromStopHook, depthOfFollowUps + 1).ConfigureAwait(False)
            End If
            Return r
        End Function

        ''' <summary>The bell, and a desktop notification when the terminal is in the background (the notifications setting: auto, bell or off).</summary>
        Private Sub Notify(Optional message As String = "Agentic is waiting for your input")
            Dim mode = State.Settings.Str("notifications", "auto")
            If mode.Equals("off", StringComparison.OrdinalIgnoreCase) Then Return
            DesktopNotify.Alert(mode, "Agentic" & If(String.IsNullOrWhiteSpace(_title), "", " · " & _title), message)
            State.Hooks.Run("Notification", "", New Dictionary(Of String, Object) From {{"message", message}})
        End Sub

        ''' <summary>While an answer runs: Esc or Ctrl+C stops it; typing queues the next message (Enter); Ctrl+O shows more.</summary>
        Private Function WatchKeys(cts As CancellationTokenSource) As Func(Of Task)
            If Not State.Interactive Then Return Function() Task.CompletedTask
            Dim stopping = False
            Dim loopTask = Task.Run(Async Function()
                                        While Not stopping
                                            If Menu.InputLock.Wait(0) Then
                                                Try
                                                    While Not stopping AndAlso SafeKeyAvailable()
                                                        RunKey(Console.ReadKey(True), cts)
                                                    End While
                                                Finally
                                                    Menu.InputLock.Release()
                                                End Try
                                            End If
                                            Await Task.Delay(40).ConfigureAwait(False)
                                        End While
                                    End Function)
            Return Async Function()
                       stopping = True
                       Await loopTask.ConfigureAwait(False)
                   End Function
        End Function

        Private Shared Function SafeKeyAvailable() As Boolean
            Try
                Return Console.KeyAvailable
            Catch ex As InvalidOperationException
                Return False
            End Try
        End Function

        Private Sub RunKey(k As ConsoleKeyInfo, cts As CancellationTokenSource)
            Dim ctrl = (k.Modifiers And ConsoleModifiers.Control) <> 0
            Dim chord = KeyBindings.Chord(k)
            If KeyBindings.Matches("stop", chord) Then
                cts.Cancel()
                Out.SpinnerText("Interrupting…")
                Return
            End If
            If KeyBindings.Matches("verbose", chord) Then
                State.Verbose = Not State.Verbose
                State.ShowThinking = State.Verbose
            ElseIf KeyBindings.Matches("queue", chord) Then
                If _typeahead.Length > 0 Then
                    SyncLock _queue
                        _queue.Enqueue(_typeahead.ToString())
                    End SyncLock
                    _typeahead.Clear()
                End If
            ElseIf k.Key = ConsoleKey.Backspace Then
                If _typeahead.Length > 0 Then _typeahead.Length -= 1
            ElseIf Not ctrl AndAlso k.KeyChar >= " "c Then
                _typeahead.Append(k.KeyChar)
            End If
            Dim extra = ""
            If _queue.Count > 0 Then extra &= Ansi.Muted($" · {_queue.Count} queued")
            If _typeahead.Length > 0 Then extra &= Ansi.Muted("  › ") & _typeahead.ToString()
            If State.Verbose Then extra &= Ansi.Muted(" · verbose")
            Out.SpinnerExtra(extra)
        End Sub

        ' ------------------------------------------------------------------ questions, plans, subagents

        ''' <summary>The model's multiple-choice questions (ask_user), one menu each; the answers go back as the next message.</summary>
        Private Function AskQuestions(questions As List(Of JsonElement)) As String
            ' Answered in the web app (a new message in this chat): the menu here closes and that message is followed.
            Menu.Abort = If(_link Is Nothing, Nothing, New Func(Of Boolean)(Function() _link.HasChatWork(AddressOf OnRemoteTitle)))
            Try
                Dim answer = AskQuestionsCore(questions)
                If Menu.Aborted Then
                    Out.Line(Ansi.Muted("  " & Ansi.Elbow & "  answered in another window"))
                    Return Nothing
                End If
                Return answer
            Finally
                Menu.Abort = Nothing
            End Try
        End Function

        Private Function AskQuestionsCore(questions As List(Of JsonElement)) As String
            Dim answers As New List(Of (Q As String, A As String))
            For Each q In questions
                Dim text = Jx.StrOf(q, "question")
                Dim opts = Jx.StringsOf(q, "options")
                Dim allowOther = Jx.BoolOf(q, "allowOther", True) OrElse opts.Count = 0
                Dim items = opts.Select(Function(x) New MenuOption(x)).ToList()
                If allowOther Then items.Add(New MenuOption("Type something else…", Nothing, ChrW(1) & "other"))
                Dim title = Ansi.Accent(Ansi.Pointer) & " " & Ansi.Bold(text) & If(questions.Count > 1, Ansi.Muted($"  ({answers.Count + 1} of {questions.Count})"), "")
                Dim pick = If(items.Count = 0, -1, Menu.Select(title, items))
                Dim answer As String
                If pick < 0 Then Return Nothing
                If items(pick).Value = ChrW(1) & "other" Then
                    answer = Menu.Ask(Ansi.Accent(Ansi.Pointer) & " Your answer:")
                    If String.IsNullOrWhiteSpace(answer) Then Return Nothing
                Else
                    answer = items(pick).Label
                End If
                answers.Add((text, answer.Trim()))
            Next
            If answers.Count = 1 Then Return answers(0).A
            Return String.Join(vbLf & vbLf, answers.Select(Function(a) $"**{a.Q}**" & vbLf & a.A))
        End Function

        Private Function ApprovePlanInteractive(plan As String) As (Choice As String, Feedback As String)
            Out.StopSpinner()
            Notify("The plan is ready for your approval")
            TurnRunner.DrawBox("Ready to code?", "  Here is Agentic's plan:" & Environment.NewLine & Environment.NewLine & MarkdownStream.Render(plan, "  ", "  ", Math.Min(Ansi.TermWidth() - 1, 120) - 4))
            Dim pick = Menu.Select(" Would you like to proceed?", {New MenuOption("Yes, and auto-accept edits", Nothing, "auto", True),
                                                                   New MenuOption("Yes, and manually approve edits", Nothing, "manual"),
                                                                   New MenuOption("No, keep planning", Nothing, "")})
            If pick = 0 Then Return ("auto", Nothing)
            If pick = 1 Then Return ("manual", Nothing)
            Dim feedback = Menu.Ask(Ansi.Accent(Ansi.Pointer) & " What should change in the plan? (Enter to skip)")
            Return ("", feedback)
        End Function

        ''' <summary>The Task tool: a subagent with its own (archived) conversation, instructions and tools; returns its report.</summary>
        Private Async Function RunSubagentAsync(type As String, description As String, prompt As String, ctx As ToolContext) As Task(Of ToolResult)
            Dim ct = ctx.Cancel
            Dim defs = Subagents.All(State.ProjectDir, _includeClaude)
            Dim def = defs.FirstOrDefault(Function(a) a.Name.Equals(If(type, ""), StringComparison.OrdinalIgnoreCase))
            If def Is Nothing Then
                If String.IsNullOrWhiteSpace(type) Then
                    def = defs.First()
                Else
                    Return ToolResult.Fail($"Unknown subagent type '{type}'. Available: {String.Join(", ", defs.Select(Function(a) a.Name))}")
                End If
            End If
            State.Hooks.Run("SubagentStart", def.Name, New Dictionary(Of String, Object) From {{"agent_type", def.Name}, {"description", description}})
            Dim sw = Stopwatch.StartNew()
            Dim subChat As String
            Try
                ' Linked to the Task call that starts it: its answer also streams into that card in the web app (and desktop, VS Code).
                Dim parent = If(String.IsNullOrEmpty(ctx.CallId) OrElse String.IsNullOrEmpty(ctx.ChatId), Nothing,
                                CObj(New With {.chatId = ctx.ChatId, .callId = ctx.CallId, .label = ToolKit.OneLine(description, 60), .type = def.Name}))
                If ctx.Fork AndAlso Not String.IsNullOrEmpty(ctx.ChatId) Then
                    ' fork: the subagent's conversation starts as a copy of this one (for side tasks that need its context).
                    subChat = (Await _chat.ForkAsync(ctx.ChatId, "Subagent: " & ToolKit.OneLine(description, 60), ct, parent:=parent).ConfigureAwait(False)).Id
                    prompt = "(Note from the app: you are a forked subagent. The conversation above is your context; do only this task, then write your report.)" &
                             vbLf & vbLf & prompt
                Else
                    subChat = (Await _chat.CreateAsync("Subagent: " & ToolKit.OneLine(description, 60), _model, _server, ct, parent:=parent).ConfigureAwait(False)).Id
                End If
                Await _chat.UpdateAsync(subChat, ct, archived:=True).ConfigureAwait(False)
            Catch ex As ApiException
                Return ToolResult.Fail("Could not start the subagent: " & ex.Message)
            End Try
            Dim model = _model
            If Not String.IsNullOrWhiteSpace(def.Model) AndAlso Not def.Model.Equals("inherit", StringComparison.OrdinalIgnoreCase) Then
                Dim m = _meta.Find(def.Model)
                If m IsNot Nothing Then model = m.Id
            End If
            Dim r = Await _runner.RunAsync(New TurnOptions With {
                .ChatId = subChat, .Content = prompt, .Model = model, .Server = _server, .Tools = BuildTools(def, ctx.Depth + 1), .Instructions = BuildInstructions(def),
                .MaxToolRounds = _maxTurns, .MaxToolRoundsHard = _maxTurnsHard, .Depth = ctx.Depth + 1, .Render = Not _printMode, .Label = ToolKit.OneLine(description, 24)}, ct).ConfigureAwait(False)
            ' Several subagents can finish at the same moment (they run in parallel).
            Interlocked.Add(_promptTokens, CLng(r.PromptTokens))
            Interlocked.Add(_completionTokens, CLng(r.CompletionTokens))
            Interlocked.Add(_toolUses, r.ToolUses)
            Dim text = r.Text.ToString().Trim()
            If r.Cancelled Then Return ToolResult.Fail("The subagent was interrupted by the user.", "Interrupted")
            If text.Length = 0 AndAlso r.Error IsNot Nothing Then Return ToolResult.Fail("The subagent failed: " & r.Error)
            Dim summary = $"Done ({r.ToolUses} tool use{If(r.ToolUses = 1, "", "s")} · {TurnRunner.FormatTokens(r.PromptTokens + r.CompletionTokens)} tokens · {sw.Elapsed.TotalSeconds:0}s)"
            Return ToolResult.Done(If(text.Length = 0, "(The subagent returned no report.)", text), summary)
        End Function

        ' ------------------------------------------------------------------ the interactive loop

        Public Async Function RunInteractiveAsync(initialPrompt As String) As Task(Of Integer)
            Try
                Console.TreatControlCAsInput = True
            Catch ex As IOException
            End Try
            Banner()
            StartAutoUpdate()
            _editor = New LineEditor(New RealTerminal()) With {
                .History = Sessions.History(State.ProjectDir), .Placeholder = Tip(), .Footer = AddressOf FooterLeft, .FooterRight = AddressOf FooterRight,
                .Suggest = AddressOf Suggest, .OnShiftTab = AddressOf CycleMode, .OnDoubleEsc = Sub() RewindMenuAsync().GetAwaiter().GetResult(),
                .OnKey = AddressOf ShortcutKey, .ExternalEdit = AddressOf EditMessageInEditor, .PasteImage = AddressOf PasteClipboardImage, .VimMode = State.Settings.Str("editorMode", "normal").Equals("vim", StringComparison.OrdinalIgnoreCase),
                .Interrupt = If(_link Is Nothing, Nothing, New Func(Of Boolean)(Function() _link.HasWork(AddressOf OnRemoteTitle)))}
            ' The account's saved prompts work as /commands (loaded in the background).
            Dim unusedPrompts = LoadSavedPromptsAsync()
            If Not String.IsNullOrWhiteSpace(initialPrompt) Then
                EchoUser(initialPrompt)
                Await HandleInputAsync(initialPrompt).ConfigureAwait(False)
            End If
            While Not _quit
                Dim text As String = Nothing
                Dim fromTimer = False
                ' A message sent to this chat from the web app (or a tool call for this CLI) comes first.
                If _link IsNot Nothing AndAlso _link.HasWork(AddressOf OnRemoteTitle) Then
                    Try
                        Await FollowRemoteAsync().ConfigureAwait(False)
                    Catch ex As ApiException
                        Out.Fail(ex.Message)
                    Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is InvalidOperationException OrElse TypeOf ex Is UnauthorizedAccessException
                        Out.Fail(ex.Message)
                    End Try
                    Continue While
                End If
                SyncLock _queue
                    If _queue.Count > 0 Then
                        text = String.Join(vbLf, _queue)
                        _queue.Clear()
                    End If
                End SyncLock
                ' Dictation mode: the next message is spoken (Nothing: it ended, and the message box is back).
                Dim dictated = False
                If text Is Nothing AndAlso _dictation Then
                    text = Await DictationTurnAsync().ConfigureAwait(False)
                    If text Is Nothing Then Continue While
                    dictated = True
                End If
                If text Is Nothing Then
                    _editor.Placeholder = Tip()
                    ' Loops and the heartbeat start while the box is empty (see Automation.vb).
                    _editor.IdleTick = If(HasIdleWork(), New Func(Of String)(AddressOf DueIdlePrompt), Nothing)
                    text = _editor.ReadLine(TakeTypeahead())
                    If text Is Nothing Then
                        ' Another window sent a message here: what was typed stays in the box for later.
                        If _editor.Interrupted Then
                            If Not String.IsNullOrEmpty(_editor.InterruptedText) Then _typeahead.Append(_editor.InterruptedText)
                            Continue While
                        End If
                        If _editor.ExitRequested Then Exit While
                        Continue While
                    End If
                    fromTimer = _editor.LastWasIdle
                End If
                If text.Trim().Length = 0 Then Continue While
                If fromTimer Then
                    Out.Line(Ansi.Info("⟳ ") & Ansi.Muted(_idleNote))
                Else
                    Sessions.AddHistory(State.ProjectDir, text)
                    EchoUser(text)
                End If
                Dim answersBefore = _answerCount
                Try
                    Await HandleInputAsync(text).ConfigureAwait(False)
                Catch ex As ApiException
                    Out.Fail(ex.Message)
                Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is InvalidOperationException OrElse TypeOf ex Is UnauthorizedAccessException
                    Out.Fail(ex.Message)
                End Try
                ' Read aloud (/read on), or Dictation mode's spoken summary of the answer.
                Dim isCommand = text.TrimStart().StartsWith("/", StringComparison.Ordinal)
                If Not isCommand OrElse dictated Then
                    Try
                        Await AfterAnswerSpeechAsync(dictated, _answerCount > answersBefore).ConfigureAwait(False)
                    Catch ex As ApiException
                        Out.Fail(ex.Message)
                    End Try
                End If
            End While
            Shutdown()
            Return 0
        End Function

        Private Function TakeTypeahead() As String
            Dim t = _typeahead.ToString()
            _typeahead.Clear()
            Return t
        End Function

        Private Async Function HandleInputAsync(text As String) As Task
            Dim trimmed = text.Trim()
            ' "stop caveman" or "normal mode" turns caveman mode off (like the original).
            Dim plainWords = trimmed.ToLowerInvariant().TrimEnd("."c, "!"c)
            If _server.Caveman IsNot Nothing AndAlso (plainWords = "stop caveman" OrElse plainWords = "normal mode") Then
                SetCaveman("off")
                Return
            End If
            If trimmed.StartsWith("/") AndAlso trimmed.Length > 1 AndAlso Not trimmed.StartsWith("//") Then
                Await CommandAsync(trimmed).ConfigureAwait(False)
            ElseIf trimmed.StartsWith("!") Then
                Await BashModeAsync(trimmed.Substring(1).Trim()).ConfigureAwait(False)
            ElseIf trimmed.StartsWith("#") AndAlso Not trimmed.StartsWith("##") AndAlso trimmed.Length > 1 AndAlso Not trimmed.Contains(vbLf) Then
                RememberAsk(trimmed.Substring(1).Trim())
            Else
                Await SendAsync(text).ConfigureAwait(False)
            End If
        End Function

        ''' <summary>"! command" runs it here; its output goes into the next message (Claude Code's bash mode).</summary>
        Private Async Function BashModeAsync(cmdText As String) As Task
            If cmdText.Length = 0 Then Return
            _runCts = New CancellationTokenSource()
            Dim stopKeys = WatchKeys(_runCts)
            Out.StartSpinner("Running…")
            Dim r As (ExitCode As Integer, Output As String, NewCwd As String, BackgroundId As String, Interrupted As Boolean, StillRunning As Boolean) = (0, "", Nothing, Nothing, False, False)
            Dim failure As Exception = Nothing
            Try
                r = Await State.Shells.RunAsync(cmdText, State.Cwd, 600000, _runCts.Token).ConfigureAwait(False)
            Catch ex As Exception
                failure = ex
            End Try
            Out.StopSpinner()
            Await stopKeys().ConfigureAwait(False)
            If failure IsNot Nothing Then Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw()
            If Not String.IsNullOrEmpty(r.NewCwd) AndAlso Directory.Exists(r.NewCwd) AndAlso State.InWorkingDirs(r.NewCwd) Then State.Cwd = Path.GetFullPath(r.NewCwd)
            Dim preview = ToolKit.PreviewLines(r.Output, If(State.Verbose, 400, 30), "  ")
            If preview IsNot Nothing Then Out.Write(preview)
            If r.StillRunning Then
                ' Not stopped after 10 minutes: it goes on in the background (BashOutput / KillShell, or /bashes).
                Out.Line(Ansi.Muted($"  still running: it goes on in the background as {r.BackgroundId}"))
                _pendingContext.Add($"<bash-input>{cmdText}</bash-input>" & vbLf & $"<bash-stdout>{ToolKit.Clip(r.Output, 20000)}</bash-stdout>" &
                                    $"<bash-background>still running in the background as {r.BackgroundId} (BashOutput / KillShell)</bash-background>")
            Else
                If r.ExitCode <> 0 Then Out.Line(Ansi.Bad($"  exit code {r.ExitCode}"))
                _pendingContext.Add($"<bash-input>{cmdText}</bash-input>" & vbLf & $"<bash-stdout>{ToolKit.Clip(r.Output, 20000)}</bash-stdout>" & If(r.ExitCode <> 0, $"<bash-exit-code>{r.ExitCode}</bash-exit-code>", ""))
            End If
            Out.Line(Ansi.Dim2("  (the output goes with your next message)"))
            Out.Line()
        End Function

        ''' <summary>"# text" saves a note to a memory file.</summary>
        Private Sub RememberAsk(text As String)
            If text.Length = 0 Then Return
            Dim pick = Menu.Select(" Where should this memory go?", {
                New MenuOption("Project memory", "AGENTS.md in this folder (checked in, shared with the team)", Path.Combine(State.ProjectDir, "AGENTS.md")),
                New MenuOption("Project memory (local)", "AGENTS.local.md (only you; add it to .gitignore)", Path.Combine(State.ProjectDir, "AGENTS.local.md")),
                New MenuOption("User memory", "~\.agentic\AGENTS.md (all your projects)", Paths.UserMemoryFile())})
            If pick < 0 Then Return
            Dim file = {Path.Combine(State.ProjectDir, "AGENTS.md"), Path.Combine(State.ProjectDir, "AGENTS.local.md"), Paths.UserMemoryFile()}(pick)
            Memory.Append(file, text)
            Out.Success($"Saved to {State.Display(file)}")
            Out.Line()
        End Sub

        ' ------------------------------------------------------------------ the message box: footer, suggestions, shortcuts

        Private Shared ReadOnly Tips As String() = {"Try ""fix the failing tests""", "Try ""how does @src/... work?""", "Try ""write a test for <file>""",
            "Try ""refactor <file> to …""", "Try ""explain this codebase""", "Try ""/init"" to create AGENTS.md", "Use ! to run a command, # to remember something",
            "Use @ to attach files", "{cycle-mode} switches modes", "{rewind} goes back to an earlier message", "Change any key with /keys"}

        Private Function Tip() As String
            ' {action} in a tip is that action's key (/keys).
            Return Regex.Replace(Tips(New Random().Next(Tips.Length)), "\{([a-z-]+)\}", Function(m) KeyBindings.Label(m.Groups(1).Value))
        End Function

        Private Function FooterLeft() As String
            Dim vim = If(_editor IsNot Nothing AndAlso _editor.VimMode, Ansi.Muted(If(_editor.VimState = "NORMAL", "-- NORMAL --", "-- INSERT --")) & "  ", "")
            Return vim & ModeFooter()
        End Function

        Private Function ModeFooter() As String
            Select Case State.Mode
                Case PermissionMode.AcceptEdits : Return Ansi.Accent("⏵⏵ accept edits on") & Ansi.Muted($" ({KeyBindings.ShortLabel("cycle-mode")} to cycle)")
                Case PermissionMode.Plan : Return Ansi.Info("⏸ plan mode on") & Ansi.Muted($" ({KeyBindings.ShortLabel("cycle-mode")} to cycle)")
                Case PermissionMode.BypassPermissions : Return Ansi.Bad("⚠ bypass permissions on") & Ansi.Muted($" ({KeyBindings.ShortLabel("cycle-mode")} to cycle)")
                Case Else : Return Ansi.Muted("? for shortcuts")
            End Select
        End Function

        ''' <summary>Footer note about a background update (updating, updated, failed).</summary>
        Private _updateNote As String

        ''' <summary>Looks for a newer CLI on the server and installs it in the background; it runs from the next start.</summary>
        Private Sub StartAutoUpdate()
            If Updater.DisabledReason(State.Settings) IsNot Nothing Then Return
            Dim api = _api
            Dim unused = Task.Run(Async Function()
                                      Try
                                          Dim info = Await Updater.CheckAsync(api, CancellationToken.None).ConfigureAwait(False)
                                          If info Is Nothing OrElse Not info.Available Then Return
                                          _updateNote = $"updating to {info.Version}…"
                                          Await Updater.InstallAsync(api, info, CancellationToken.None).ConfigureAwait(False)
                                          _updateNote = $"updated to {info.Version} · restart agentic to use it"
                                      Catch ex As Exception When TypeOf ex Is ApiException OrElse TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException OrElse
                                                                 TypeOf ex Is InvalidDataException OrElse TypeOf ex Is Net.Http.HttpRequestException OrElse TypeOf ex Is OperationCanceledException
                                          If _updateNote IsNot Nothing Then _updateNote = "update failed · run agentic update"
                                      End Try
                                  End Function)
        End Sub

        Private Function FooterRight() As String
            Dim custom = StatusLineCommand()
            If custom IsNot Nothing Then Return custom
            Dim parts As New List(Of String)
            Dim m = _meta.Find(_model)
            parts.Add(Ansi.Muted(If(m?.Name, _model)))
            ' The last answer's average speed, like the Chat tab's speed next to the model.
            If _lastTps >= 1 Then parts.Add(Ansi.Muted($"{_lastTps:0} tok/s"))
            If _server.Caveman IsNot Nothing Then parts.Add(Ansi.Info(If(_server.Caveman = "ultra", "🪨 ultracave", If(_server.Caveman = "wenyan", "🪨 megacave", "🪨 caveman"))))
            If _ctx IsNot Nothing AndAlso _ctx.Max > 0 Then
                Dim pct = _ctx.Percent
                Dim left = Math.Max(0, _ctx.AutoCompactPercent - pct)
                parts.Add(If(pct >= 70, Ansi.Warn($"{pct:0}% context"), Ansi.Muted($"{pct:0}% context")) & If(left < 15, Ansi.Warn($" ({left:0}% until auto-compact)"), ""))
            End If
            Dim bg = State.Shells.All().Where(Function(s) s.Running).Count()
            If bg > 0 Then parts.Add(Ansi.Info($"{bg} background shell{If(bg = 1, "", "s")}"))
            If State.Verbose Then parts.Add(Ansi.Muted("verbose"))
            If _loops.Count > 0 Then parts.Add(Ansi.Info($"⟳ {_loops.Count} loop{If(_loops.Count = 1, "", "s")}"))
            If _heartbeatEvery.HasValue Then parts.Add(Ansi.Info("♥ heartbeat"))
            If _updateNote IsNot Nothing Then parts.Add(Ansi.Info(_updateNote))
            Return String.Join(Ansi.Dim2(" · "), parts)
        End Function

        ''' <summary>settings statusLine {"type":"command","command":"..."}: its first output line replaces the right side.</summary>
        Private Function StatusLineCommand() As String
            Dim cmdText = State.Settings.Str("statusLine.command")
            If cmdText Is Nothing Then Return Nothing
            If (DateTime.UtcNow - _statusLineCache.At).TotalSeconds < 3 Then Return _statusLineCache.Text
            Dim json = Jx.Stringify(New With {.session_id = State.ChatId, .cwd = State.Cwd, .model = New With {.id = _model, .display_name = If(_meta.Find(_model)?.Name, _model)},
                                              .workspace = New With {.current_dir = State.Cwd, .project_dir = State.ProjectDir}, .permission_mode = State.ModeLabel,
                                              .context = If(_ctx Is Nothing, Nothing, New With {.used = _ctx.Used, .max = _ctx.Max, .percent = _ctx.Percent})})
            Dim text As String = Nothing
            Try
                Dim psi = Platform.ShellStart(cmdText)
                psi.CreateNoWindow = True
                psi.RedirectStandardInput = True
                psi.RedirectStandardOutput = True
                psi.StandardOutputEncoding = New UTF8Encoding(False)
                psi.WorkingDirectory = State.ProjectDir
                Using p = Process.Start(psi)
                    p.StandardInput.Write(json)
                    p.StandardInput.Close()
                    Dim outTask = p.StandardOutput.ReadToEndAsync()
                    If p.WaitForExit(1500) Then text = outTask.GetAwaiter().GetResult().Split(ChrW(10)).FirstOrDefault()?.TrimEnd(ChrW(13))
                End Using
            Catch ex As Exception When TypeOf ex Is ComponentModel.Win32Exception OrElse TypeOf ex Is IOException OrElse TypeOf ex Is InvalidOperationException
            End Try
            _statusLineCache = (text, DateTime.UtcNow)
            Return text
        End Function

        ''' <summary>/vim: Vim keys in the message box (saved in your settings, like Claude Code's editor mode).</summary>
        Private Sub ToggleVim()
            If _editor Is Nothing Then
                Out.Note("Vim mode is for the interactive message box.")
                Return
            End If
            _editor.VimMode = Not _editor.VimMode
            State.Settings.SetValue("user", "editorMode", System.Text.Json.Nodes.JsonValue.Create(If(_editor.VimMode, "vim", "normal")))
            Out.Success(If(_editor.VimMode, "Vim mode on: Esc for normal mode (h j k l w b e 0 $ x dd dw cw D C p u), i / a / A / o to type.", "Vim mode off."))
        End Sub

        Private Sub CycleMode()
            ' default → acceptEdits → plan → default (bypass only when started with it)
            Select Case State.Mode
                Case PermissionMode.Default : State.Mode = PermissionMode.AcceptEdits
                Case PermissionMode.AcceptEdits : State.Mode = PermissionMode.Plan
                Case PermissionMode.Plan : State.Mode = If(_opts.SkipPermissions OrElse State.Settings.Str("permissions.defaultMode", "") = "bypassPermissions", PermissionMode.BypassPermissions, PermissionMode.Default)
                Case Else : State.Mode = PermissionMode.Default
            End Select
        End Sub

        Private Function ShortcutKey(k As ConsoleKeyInfo) As Boolean
            Dim chord = KeyBindings.Chord(k)
            If KeyBindings.Matches("verbose", chord) Then
                State.Verbose = Not State.Verbose
                State.ShowThinking = State.Verbose
                _editor.Flash(If(State.Verbose, "Verbose: full tool output and thinking", "Verbose off"))
                Return True
            End If
            If KeyBindings.Matches("todos", chord) Then
                _editor.PrintAbove(Ansi.Bold("Todos") & Environment.NewLine & TodoWriteTool.Render(State.Todos, "  "))
                Return True
            End If
            Return False
        End Function

        Private Function Suggest(buffer As String, pos As Integer) As (Items As List(Of Suggestion), Start As Integer)
            Dim before = buffer.Substring(0, Math.Min(pos, buffer.Length))
            If before.StartsWith("/") AndAlso Not before.Contains(" "c) AndAlso Not before.Contains(ChrW(10)) Then
                Dim q = before.Substring(1).ToLowerInvariant()
                Dim all = CommandCatalog()
                Dim ranked = all.Where(Function(c) c.Name.ToLowerInvariant().StartsWith(q)).Concat(
                             all.Where(Function(c) Not c.Name.ToLowerInvariant().StartsWith(q) AndAlso (c.Name.ToLowerInvariant().Contains(q) OrElse c.Description.ToLowerInvariant().Contains(q)))).
                             Take(40).Select(Function(c) New Suggestion With {.Text = "/" & c.Name & If(c.TakesArgs, " ", ""), .Label = "/" & c.Name & If(c.ArgHint Is Nothing, "", " " & c.ArgHint),
                                                                             .Hint = c.Description, .Submit = True}).ToList()
                Return (ranked, 0)
            End If
            ' @file completion: the word before the cursor.
            Dim start = before.Length
            While start > 0 AndAlso Not Char.IsWhiteSpace(before(start - 1))
                start -= 1
            End While
            Dim token = before.Substring(start)
            If token.StartsWith("@") Then Return (FileSuggestions(token.Substring(1).Trim(""""c)), start)
            Return (Nothing, 0)
        End Function

        Private Function FileSuggestions(query As String) As List(Of Suggestion)
            If _fileIndex Is Nothing OrElse (DateTime.UtcNow - _fileIndexAt).TotalSeconds > 30 Then
                Dim list As New List(Of String)
                Using cts As New CancellationTokenSource(1500)
                    For Each e In New FileWalker(State.Cwd).Walk(True, cts.Token)
                        list.Add(Path.GetRelativePath(State.Cwd, e.FullPath).Replace("\"c, "/"c) & If(e.IsDir, "/", ""))
                        If list.Count >= 30000 Then Exit For
                    Next
                End Using
                _fileIndex = list
                _fileIndexAt = DateTime.UtcNow
            End If
            Dim q = query.Replace("\"c, "/"c).ToLowerInvariant()
            Dim scored As New List(Of (Path As String, Score As Integer))
            For Each p In _fileIndex
                Dim lower = p.ToLowerInvariant()
                Dim baseName = IO.Path.GetFileName(lower.TrimEnd("/"c))
                Dim score As Integer
                If q.Length = 0 Then
                    score = p.Count(Function(c) c = "/"c) * 10 + p.Length \ 20
                ElseIf baseName.StartsWith(q) Then
                    score = 0
                ElseIf lower.StartsWith(q) Then
                    score = 1
                ElseIf baseName.Contains(q) Then
                    score = 2
                ElseIf lower.Contains(q) Then
                    score = 3
                ElseIf IsSubsequence(q, lower) Then
                    score = 5
                Else
                    Continue For
                End If
                scored.Add((p, score))
            Next
            Return scored.OrderBy(Function(s) s.Score).ThenBy(Function(s) s.Path.Length).Take(15).
                Select(Function(s) New Suggestion With {.Text = "@" & If(s.Path.Contains(" "c), """" & s.Path & """", s.Path) & If(s.Path.EndsWith("/"), "", " "), .Label = s.Path,
                                                        .Hint = If(s.Path.EndsWith("/"), "folder", "")}).ToList()
        End Function

        Private Shared Function IsSubsequence(q As String, s As String) As Boolean
            Dim i = 0
            For Each c In s
                If i < q.Length AndAlso c = q(i) Then i += 1
            Next
            Return i = q.Length
        End Function

        ' ------------------------------------------------------------------ banner and shutdown

        Private Sub Banner()
            Dim w = Math.Min(Ansi.TermWidth() - 1, 70)
            Dim b = Ansi.Box
            Dim line = Function(t As String) Ansi.Brand(b(5)) & " " & Ansi.Pad(Ansi.Truncate(t, w - 4), w - 4) & " " & Ansi.Brand(b(5))
            Out.Line(Ansi.Brand(b(0) & New String(b(4)(0), w - 2) & b(1)))
            Out.Line(line(Ansi.Brand(Ansi.Star) & " Welcome to " & Ansi.Bold("Agentic CLI") & Ansi.Muted(" v" & AppInfo.Version)))
            Out.Line(line(""))
            Out.Line(line(Ansi.Muted("  /help for help, /status for your current setup")))
            Out.Line(line(""))
            Out.Line(line(Ansi.Muted("  cwd: ") & State.Display(State.ProjectDir)))
            Dim m = _meta.Find(_model)
            Out.Line(line(Ansi.Muted("  model: ") & If(m?.Name, If(_model, "(none)")) & Ansi.Muted("  ·  server: " & _creds.ServerUrl)))
            Out.Line(Ansi.Brand(b(2) & New String(b(4)(0), w - 2) & b(3)))
            If Not File.Exists(Path.Combine(State.ProjectDir, "AGENTS.md")) AndAlso Not File.Exists(Path.Combine(State.ProjectDir, "CLAUDE.md")) Then
                Out.Line()
                Out.Line(Ansi.Muted(" Tips for getting started:"))
                Out.Line(Ansi.Muted(" 1. Run /init to create an AGENTS.md file with instructions for Agentic"))
                Out.Line(Ansi.Muted(" 2. Ask questions, edit code, run commands; use @ to mention files"))
                Out.Line(Ansi.Muted($" 3. {KeyBindings.Label("cycle-mode")} switches between normal, accept-edits and plan mode (change keys with /keys)"))
            End If
            If State.Mode = PermissionMode.BypassPermissions Then Out.Warning("Bypass permissions mode: every tool runs without asking.")
            Out.Line()
        End Sub

        Private Sub Shutdown()
            Try
                State.Hooks.Run("SessionEnd", "exit", New Dictionary(Of String, Object) From {{"reason", "exit"}})
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is InvalidOperationException
            End Try
            _projectRuns?.StopAll()
            State.Shells.KillAll()
            State.Monitors?.Dispose()
            DeletePastedImages()
            Out.Raw(Ansi.Title(""))
        End Sub

        Public Sub Dispose() Implements IDisposable.Dispose
            _link?.Dispose()
            Try
                _projectRuns?.StopAll()
                State.Shells?.KillAll()
                State.Mcp?.Dispose()
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is InvalidOperationException
            End Try
            _api?.Dispose()
        End Sub

    End Class

End Namespace
