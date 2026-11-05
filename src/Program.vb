Option Strict On
Option Explicit On
Option Infer On

Imports System.Collections.Concurrent
Imports System.IO
Imports System.Text
Imports System.Text.Json
Imports System.Threading
Imports System.Threading.Channels
Imports AgenticCli.Agent
Imports AgenticCli.Api
Imports AgenticCli.Core
Imports AgenticCli.Ui

Friend Module Program

    Function Main(args As String()) As Integer
        Dim o = CliOptions.Parse(args)
        Ansi.Init(If(o.NoColor, New Boolean?(False), New Boolean?()), Environment.GetEnvironmentVariable("AGENTIC_GLYPHS"))
        If o.Errors.Count > 0 Then
            Console.Error.WriteLine(String.Join(Environment.NewLine, o.Errors) & Environment.NewLine & "Run agentic --help for the options.")
            Return 2
        End If
        If o.Help OrElse o.Command = "help" Then
            Console.WriteLine(CliOptions.Usage)
            Return 0
        End If
        If o.Version OrElse o.Command = "version" Then
            Console.WriteLine($"{AppInfo.Version} (Agentic CLI)")
            Return 0
        End If
        If o.SelfTest Then Return SelfTest.Run()
        Try
            Return MainAsync(o).GetAwaiter().GetResult()
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException OrElse TypeOf ex Is InvalidOperationException
            Console.Error.WriteLine("agentic: " & ex.Message)
            Return 1
        End Try
    End Function

    Private Async Function MainAsync(o As CliOptions) As Task(Of Integer)
        Select Case o.Command
            Case "login", "setup"
                Return If(Await LoginAsync(Credentials.Load()).ConfigureAwait(False) Is Nothing, 1, 0)
            Case "logout"
                Credentials.Forget()
                Console.WriteLine("The API key was removed from this computer.")
                Return 0
            Case "auth"
                ' agentic auth status|login|logout (Claude Code's `claude auth …`).
                Select Case If(o.CommandArgs.FirstOrDefault(), "status").ToLowerInvariant()
                    Case "login" : Return If(Await LoginAsync(Credentials.Load()).ConfigureAwait(False) Is Nothing, 1, 0)
                    Case "logout"
                        Credentials.Forget()
                        Console.WriteLine("The API key was removed from this computer.")
                        Return 0
                    Case "status" : Return Await AuthStatusAsync().ConfigureAwait(False)
                    Case Else
                        Console.Error.WriteLine("Usage: agentic auth status|login|logout")
                        Return 2
                End Select
            Case "mcp" : Return Await McpCli.RunAsync(o.CommandArgs).ConfigureAwait(False)
            Case "config" : Return ConfigCli.Run(o.CommandArgs)
            Case "sessions"
                For Each s In Sessions.List(Environment.CurrentDirectory)
                    Console.WriteLine($"{s.ChatId}  {Session.Ago(s.UpdatedUtc),-12} {If(String.IsNullOrWhiteSpace(s.Title), s.LastPrompt, s.Title)}")
                Next
                Return 0
            Case "agents"
                For Each a In Subagents.All(Environment.CurrentDirectory, True)
                    Console.WriteLine($"{a.Name,-22} {a.Source,-12} {a.Description}")
                Next
                Return 0
            Case "skills"
                For Each s In New Skills.SkillStore(Environment.CurrentDirectory, True).All()
                    Console.WriteLine($"/{s.Name,-28} {s.Kind,-8} {s.Source,-16} {s.Description}")
                Next
                Return 0
            Case "update", "upgrade"
                Return Await UpdateCommandAsync(o.CommandArgs.Contains("--check")).ConfigureAwait(False)
        End Select
        Updater.CleanUp()

        Dim creds = Credentials.Load()
        If o.Command = "doctor" Then Return Await Doctor.RunAsync(creds, Nothing, Nothing).ConfigureAwait(False)
        If Not creds.IsConfigured() Then
            If o.Print OrElse Not Ansi.Interactive Then
                Console.Error.WriteLine("Not connected: run ""agentic login"" first, or set AGENTIC_SERVER and AGENTIC_API_KEY.")
                Return 1
            End If
            creds = Await LoginAsync(creds).ConfigureAwait(False)
            If creds Is Nothing Then Return 1
        End If

        ' Piped input: with -p it joins the prompt; without -p it makes the session non-interactive too.
        Dim streamInput = o.InputFormat = "stream-json"
        Dim stdin As String = Nothing
        If Console.IsInputRedirected AndAlso Not streamInput Then stdin = Await Console.In.ReadToEndAsync().ConfigureAwait(False)
        Dim headless = streamInput OrElse o.Print OrElse Console.IsInputRedirected OrElse Console.IsOutputRedirected AndAlso o.Prompt IsNot Nothing
        ' -i/--image: the images go with the first message as @ mentions (full paths, before --worktree moves the folder).
        If o.Images.Count > 0 Then
            Dim missing = o.Images.FirstOrDefault(Function(f) Not IO.File.Exists(f))
            If missing IsNot Nothing Then
                Console.Error.WriteLine("--image: no such file: " & missing)
                Return 2
            End If
            o.Prompt = (If(o.Prompt, "") & " " & String.Join(" ", o.Images.Select(Function(f) "@""" & IO.Path.GetFullPath(f) & """"))).Trim()
        End If
        ' -w/--worktree: the session works in its own git worktree (a separate folder on its own branch).
        If o.Worktree IsNot Nothing Then
            Dim wt = Worktrees.Create(Environment.CurrentDirectory, o.Worktree)
            If wt.Error IsNot Nothing Then
                Console.Error.WriteLine("--worktree: " & wt.Error)
                Return 1
            End If
            Environment.CurrentDirectory = wt.Folder
            If Not headless Then Console.WriteLine(Ansi.Muted($"Working in the worktree {wt.Folder} (branch {wt.Branch})."))
        End If
        Using session As New Session(o, creds)
            If Not Await session.InitAsync(headless).ConfigureAwait(False) Then Return 1
            If streamInput Then Return Await PrintMode.RunStreamAsync(session, o).ConfigureAwait(False)
            If headless Then
                Dim prompt = If(o.Prompt, "")
                If Not String.IsNullOrWhiteSpace(stdin) Then prompt = If(prompt.Length > 0, prompt & vbLf & vbLf & stdin, stdin)
                If String.IsNullOrWhiteSpace(prompt) Then
                    Console.Error.WriteLine("No prompt: give one as an argument or on stdin.")
                    Return 2
                End If
                Return Await PrintMode.RunAsync(session, prompt, o).ConfigureAwait(False)
            End If
            Return Await session.RunInteractiveAsync(o.Prompt).ConfigureAwait(False)
        End Using
    End Function

    ''' <summary>agentic auth status: the server, whether a key is stored, and who the server says it belongs to (exit 1 when not signed in).</summary>
    Private Async Function AuthStatusAsync() As Task(Of Integer)
        Dim creds = Credentials.Load()
        If Not creds.IsConfigured() Then
            Console.WriteLine("Not signed in. Run ""agentic login"" (or set AGENTIC_SERVER and AGENTIC_API_KEY).")
            Return 1
        End If
        Console.WriteLine("Server:  " & creds.ServerUrl)
        Dim key = If(creds.ApiKey, "")
        Console.WriteLine("API key: " & If(key.Length > 8, key.Substring(0, 6) & "…" & key.Substring(key.Length - 4), "(set)"))
        Try
            Using api As New ApiClient(creds.ServerUrl, creds.ApiKey)
                Dim who = Await api.GetAsync("/api/auth/me", CancellationToken.None, 15).ConfigureAwait(False)
                Console.WriteLine($"Signed in as {Jx.StrOf(who, "user", "?")} ({Jx.StrOf(who, "role", "user")})" &
                                  If(Jx.BoolOf(who, "charged"), $" · balance ${Jx.LongOf(who, "balanceMicros") / 1000000D:0.00}", ""))
                Return 0
            End Using
        Catch ex As ApiException
            Console.WriteLine("The server did not accept the key: " & ex.Message)
            Return 1
        End Try
    End Function

    ''' <summary>agentic update [--check]: installs the server's newer build now (or only says whether there is one).</summary>
    Private Async Function UpdateCommandAsync(checkOnly As Boolean) As Task(Of Integer)
        Updater.CleanUp()
        Dim creds = Credentials.Load()
        If Not creds.IsConfigured() Then
            Console.Error.WriteLine("Not connected: run ""agentic login"" first.")
            Return 1
        End If
        Using api As New ApiClient(creds.ServerUrl, creds.ApiKey)
            Dim info As UpdateInfo
            Try
                info = Await Updater.CheckAsync(api, CancellationToken.None).ConfigureAwait(False)
            Catch ex As ApiException
                Out.Fail("Could not check for updates: " & ex.Message)
                Return 1
            End Try
            If info Is Nothing Then
                Out.Note($"{creds.ServerUrl} publishes no Agentic CLI build (Apps & updates in the web app).")
                Return 0
            End If
            If Not info.Available Then
                Out.Success($"Agentic CLI {AppInfo.Version} is up to date" & If(info.Reason = "up to date", ".", $" ({info.Reason})."))
                Return 0
            End If
            Out.Line($"Agentic CLI {AppInfo.Version} → {info.Version}: {info.Reason}.")
            If Not String.IsNullOrWhiteSpace(info.Notes) Then Out.Line(Ansi.Muted("  " & info.Notes.Replace(vbLf, vbLf & "  ")))
            If checkOnly Then Return 0
            If Not Updater.IsUpdatableBuild() Then
                Out.Fail("This is a development build; it does not replace itself. Publish one (dotnet publish) or download the exe from the web app.")
                Return 1
            End If
            Out.StartSpinner("Downloading…")
            Try
                Await Updater.InstallAsync(api, info, CancellationToken.None, Sub(done, total) Out.SpinnerText(If(total > 0, $"Downloading… {done * 100 \ total}%", "Downloading…"))).ConfigureAwait(False)
                Out.StopSpinner()
            Catch ex As Exception When TypeOf ex Is ApiException OrElse TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException OrElse TypeOf ex Is InvalidDataException
                Out.StopSpinner()
                Out.Fail("The update failed: " & ex.Message)
                Return 1
            End Try
            Out.Success($"Updated to {info.Version}. It runs from the next start of agentic.")
            Return 0
        End Using
    End Function

    ''' <summary>
    ''' Picks the server (the built-in address unless the user chooses another), asks for the API key, checks them and saves
    ''' them to config.json next to agentic.exe.
    ''' </summary>
    Public Async Function LoginAsync(existing As Credentials) As Task(Of Credentials)
        Out.Line(Ansi.Brand(Ansi.Star) & " " & Ansi.Bold("Connect Agentic CLI to your Agentic server"))
        Out.Line(Ansi.Muted("  Create an API key in the web app: user menu → API key → Generate."))
        Dim servers As New List(Of MenuOption) From {New MenuOption(Credentials.DefaultServer, "default", Credentials.DefaultServer)}
        If Not String.Equals(existing.ServerUrl, Credentials.DefaultServer, StringComparison.OrdinalIgnoreCase) Then
            servers.Add(New MenuOption(existing.ServerUrl, "current", existing.ServerUrl))
        End If
        servers.Add(New MenuOption("Another address…", "type a custom server URL", ""))
        Dim pick = Menu.Select("Server", servers, 0)
        If pick < 0 Then Return Nothing
        Dim url = servers(pick).Value
        If url.Length = 0 Then
            url = Menu.Ask("Server address (http://host:port):")
            If String.IsNullOrWhiteSpace(url) Then Return Nothing
        End If
        url = url.Trim().TrimEnd("/"c)
        If Not url.StartsWith("http", StringComparison.OrdinalIgnoreCase) Then url = "http://" & url
        Dim keepKey = existing.ApiKey.Length > 0 AndAlso String.Equals(url, existing.ServerUrl, StringComparison.OrdinalIgnoreCase)
        Dim key = Menu.Ask(If(keepKey, "API key (agk_…, Enter keeps " & existing.MaskedKey() & "):", "API key (agk_…):"), "", secret:=True)
        If key Is Nothing Then Return Nothing
        If String.IsNullOrWhiteSpace(key) AndAlso keepKey Then key = existing.ApiKey
        If String.IsNullOrWhiteSpace(key) Then
            Out.Fail("No API key given.")
            Return Nothing
        End If
        Dim c As New Credentials With {.ServerUrl = url, .ApiKey = key.Trim()}
        Out.StartSpinner("Checking…")
        Try
            Using api As New ApiClient(c.ServerUrl, c.ApiKey)
                Dim meta = Await New ChatApi(api).MetaAsync(CancellationToken.None).ConfigureAwait(False)
                Out.StopSpinner()
                Out.Success($"Connected: {meta.AllModels().Count} models" & If(meta.CanTools, ", server tools available", ""))
            End Using
        Catch ex As ApiException
            Out.StopSpinner()
            Out.Fail(ex.Message)
            If Not Menu.Confirm(" Save anyway?", False) Then Return Nothing
        End Try
        Try
            Out.Note("  Saved in " & c.Save())
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
            Out.Fail($"Could not write {Credentials.ConfigFile()}: {ex.Message}")
            Return Nothing
        End Try
        Return c
    End Function

End Module

''' <summary>-p / --print: one answer for scripts and pipes, as text, JSON or a JSON event stream.</summary>
Friend NotInheritable Class PrintMode

    Private Sub New()
    End Sub

    Public Shared Async Function RunAsync(session As Session, prompt As String, o As CliOptions) As Task(Of Integer)
        Dim format = o.OutputFormat
        Dim sw = Stopwatch.StartNew()
        Dim emit = Sub(obj As Object)
                       Console.Out.WriteLine(Jx.Stringify(obj))
                       Console.Out.Flush()
                   End Sub
        Dim observer As Action(Of String, Object) = Nothing
        If format = "stream-json" Then
            emit(New With {.type = "system", .subtype = "init", .cwd = session.State.Cwd, .model = session.Model, .permissionMode = session.State.ModeLabel,
                           .tools = session.BuildTools().Select(Function(t) t.Name).ToList()})
            Dim buffer As New StringBuilder()
            observer = Sub(kind, data)
                           Select Case kind
                               Case "text" : emit(New With {.type = "assistant", .text = data})
                               Case "tool_use" : emit(New With {.type = "tool_use", .tool = data})
                               Case "tool_result" : emit(New With {.type = "tool_result", .result = data})
                           End Select
                       End Sub
        ElseIf o.Verbose AndAlso format = "text" Then
            observer = Sub(kind, data)
                           If kind = "tool_use" Then Console.Error.WriteLine("● " & Jx.Stringify(data))
                       End Sub
        End If
        Out.Silent = True
        Dim r = Await session.SendAsync(prompt, observer).ConfigureAwait(False)
        Out.Silent = False
        Dim text = If(r Is Nothing, "", r.Text.ToString().Trim())
        Dim isError = r Is Nothing OrElse r.Error IsNot Nothing OrElse r.Cancelled
        Select Case format
            Case "json", "stream-json"
                emit(New With {.type = "result", .subtype = If(isError, "error", "success"), .is_error = isError, .result = text,
                               .error = r?.Error, .session_id = session.State.ChatId, .duration_ms = sw.ElapsedMilliseconds, .num_turns = If(r?.Rounds, 0),
                               .stop_reason = r?.StopReason, .usage = New With {.input_tokens = If(r?.PromptTokens, 0), .output_tokens = If(r?.CompletionTokens, 0)},
                               .questions = If(r Is Nothing, Nothing, r.Questions)})
            Case Else
                If text.Length > 0 Then Console.Out.WriteLine(text)
                If r?.Error IsNot Nothing Then Console.Error.WriteLine("agentic: " & r.Error)
                If r IsNot Nothing AndAlso r.Questions.Count > 0 Then
                    For Each q In r.Questions
                        Console.Out.WriteLine("? " & Jx.StrOf(q, "question") & " " & String.Join(" / ", Jx.StringsOf(q, "options")))
                    Next
                End If
        End Select
        Return If(isError, 1, 0)
    End Function

    ''' <summary>
    ''' --input-format stream-json: another program drives the CLI. It writes one JSON message per line on stdin and reads one JSON
    ''' event per line on stdout (protocol 1, see README "Driving the CLI from your own programs").
    '''   in:  user · control (interrupt, set_model, set_permission_mode, set_effort, new_conversation, resume) ·
    '''        register_tools · permission_response · tool_result
    '''   out: system/init · assistant · tool_use · tool_result · permission_request · host_tool_call · result · control_response · error
    ''' </summary>
    Public Shared Async Function RunStreamAsync(session As Session, o As CliOptions) As Task(Of Integer)
        Dim gate As New Object()
        Dim emit As Action(Of Object) = Sub(obj)
                                            Dim line = Jx.Stringify(obj)
                                            SyncLock gate
                                                Console.Out.WriteLine(line)
                                                Console.Out.Flush()
                                            End SyncLock
                                        End Sub
        Dim permissions As New ConcurrentDictionary(Of String, TaskCompletionSource(Of JsonElement))
        Dim hostCalls As New ConcurrentDictionary(Of String, TaskCompletionSource(Of JsonElement))
        Dim inbox = Channel.CreateUnbounded(Of String)()
        If Not String.IsNullOrWhiteSpace(o.Prompt) Then inbox.Writer.TryWrite(o.Prompt)
        Dim releaseAll = Sub(message As String)
                             For Each kv In permissions
                                 kv.Value.TrySetResult(Jx.Parse(Jx.Stringify(New With {.allow = False, .message = message})))
                             Next
                             For Each kv In hostCalls
                                 kv.Value.TrySetResult(Jx.Parse(Jx.Stringify(New With {.ok = False, .content = message})))
                             Next
                         End Sub
        Out.Silent = True

        If If(o.PermissionPrompt, "host") <> "deny" Then
            session.State.PermissionAsker =
                Function(toolName As String, input As JsonElement, title As String, reason As String) As (Allowed As Boolean, Message As String, Remember As Boolean)
                    Dim id = "perm_" & Guid.NewGuid().ToString("N").Substring(0, 12)
                    Dim tcs As New TaskCompletionSource(Of JsonElement)(TaskCreationOptions.RunContinuationsAsynchronously)
                    permissions(id) = tcs
                    emit(New With {.type = "permission_request", .id = id, .tool = toolName, .input = input, .title = title, .reason = reason})
                    ' The turn waits here until the host answers, interrupts or goes away.
                    Dim answer = tcs.Task.GetAwaiter().GetResult()
                    permissions.TryRemove(id, Nothing)
                    Return (Jx.BoolOf(answer, "allow"), Jx.StrOf(answer, "message", Nothing), Jx.BoolOf(answer, "remember"))
                End Function
        End If
        session.HostToolInvoker =
            Async Function(name As String, input As JsonElement, ct As CancellationToken) As Task(Of Tools.ToolResult)
                Dim id = "host_" & Guid.NewGuid().ToString("N").Substring(0, 12)
                Dim tcs As New TaskCompletionSource(Of JsonElement)(TaskCreationOptions.RunContinuationsAsynchronously)
                hostCalls(id) = tcs
                emit(New With {.type = "host_tool_call", .id = id, .name = name, .input = input})
                Dim answer As JsonElement
                Using ct.Register(Sub() tcs.TrySetResult(Jx.Parse("{""ok"":false,""content"":""Interrupted.""}")))
                    answer = Await tcs.Task.ConfigureAwait(False)
                End Using
                hostCalls.TryRemove(id, Nothing)
                Dim content = Jx.StrOf(answer, "content", "")
                If Jx.Prop(answer, "content").ValueKind = JsonValueKind.Object OrElse Jx.Prop(answer, "content").ValueKind = JsonValueKind.Array Then
                    content = Jx.Prop(answer, "content").GetRawText()
                End If
                Return If(Jx.BoolOf(answer, "ok", True), Tools.ToolResult.Done(content), Tools.ToolResult.Fail(content))
            End Function

        Dim observer As Action(Of String, Object) =
            Sub(kind, data)
                Select Case kind
                    Case "text" : emit(New With {.type = "assistant", .text = data})
                    Case "tool_use" : emit(New With {.type = "tool_use", .tool = data})
                    Case "tool_result" : emit(New With {.type = "tool_result", .result = data})
                End Select
            End Sub
        emit(New With {.type = "system", .subtype = "init", .protocol = 1, .version = AppInfo.Version, .cwd = session.State.Cwd, .model = session.Model,
                       .permissionMode = session.State.ModeLabel, .session_id = session.State.ChatId,
                       .tools = session.BuildTools().Select(Function(t) t.Name).ToList()})

        Dim reader = Task.Run(
            Async Function()
                While True
                    Dim line = Await Console.In.ReadLineAsync().ConfigureAwait(False)
                    If line Is Nothing Then Exit While
                    If line.Trim().Length = 0 Then Continue While
                    Dim msg As JsonElement
                    If Not Jx.TryParse(line, msg) OrElse msg.ValueKind <> JsonValueKind.Object Then
                        emit(New With {.type = "error", .message = "Each line must be one JSON object."})
                        Continue While
                    End If
                    Dim kind = Jx.StrOf(msg, "type")
                    Dim pending As TaskCompletionSource(Of JsonElement) = Nothing
                    Select Case kind
                        Case "user"
                            Dim text = MessageText(msg)
                            If String.IsNullOrWhiteSpace(text) Then
                                emit(New With {.type = "error", .message = "A user message needs text."})
                            Else
                                ' --replay-user-messages: the host sees each message it sent, in order with the answers.
                                If o.ReplayUserMessages Then emit(New With {.type = "user", .text = text})
                                Await inbox.Writer.WriteAsync(text).ConfigureAwait(False)
                            End If
                        Case "permission_response"
                            If permissions.TryGetValue(Jx.StrOf(msg, "id"), pending) Then
                                pending.TrySetResult(msg)
                            Else
                                emit(New With {.type = "error", .message = "No permission request is waiting with that id."})
                            End If
                        Case "tool_result"
                            If hostCalls.TryGetValue(Jx.StrOf(msg, "id"), pending) Then
                                pending.TrySetResult(msg)
                            Else
                                emit(New With {.type = "error", .message = "No host tool call is waiting with that id."})
                            End If
                        Case "register_tools"
                            Dim names = session.SetHostTools(Jx.ArrOf(msg, "tools"))
                            emit(New With {.type = "control_response", .action = "register_tools", .ok = True, .tools = names})
                        Case "control"
                            If Jx.StrOf(msg, "action", Jx.StrOf(msg, "subtype")) = "interrupt" Then releaseAll("Interrupted by the user.")
                            Await ControlAsync(session, msg, emit).ConfigureAwait(False)
                        Case Else
                            emit(New With {.type = "error", .message = $"Unknown message type '{kind}'."})
                    End Select
                End While
                inbox.Writer.TryComplete()
                ' The host went away: release anything still waiting for it.
                session.Interrupt()
                releaseAll("The host program closed.")
            End Function)

        Dim exitCode = 0
        While Await inbox.Reader.WaitToReadAsync().ConfigureAwait(False)
            Dim text As String = Nothing
            While inbox.Reader.TryRead(text)
                Dim sw = Stopwatch.StartNew()
                Dim r As TurnResult = Nothing
                Dim failure As String = Nothing
                Try
                    r = Await session.SendAsync(text, observer).ConfigureAwait(False)
                Catch ex As Exception
                    failure = ex.Message
                End Try
                Dim cancelled = r IsNot Nothing AndAlso r.Cancelled
                Dim isError = failure IsNot Nothing OrElse r Is Nothing OrElse r.Error IsNot Nothing
                If isError Then exitCode = 1
                emit(New With {.type = "result", .subtype = If(cancelled, "interrupted", If(isError, "error", "success")), .is_error = isError,
                               .result = If(r Is Nothing, "", r.Text.ToString().Trim()), .error = If(failure, r?.Error), .session_id = session.State.ChatId,
                               .duration_ms = sw.ElapsedMilliseconds, .num_turns = If(r Is Nothing, 0, r.Rounds), .stop_reason = r?.StopReason,
                               .usage = New With {.input_tokens = If(r Is Nothing, 0, r.PromptTokens), .output_tokens = If(r Is Nothing, 0, r.CompletionTokens)},
                               .questions = If(r Is Nothing, Nothing, r.Questions)})
            End While
        End While
        Await reader.ConfigureAwait(False)
        Return exitCode
    End Function

    ''' <summary>Text of a user message: "message" as a string, {content: string} or Claude-style [{type:"text", text}] blocks.</summary>
    Friend Shared Function MessageText(msg As JsonElement) As String
        Dim m = Jx.Prop(msg, "message")
        If m.ValueKind = JsonValueKind.Undefined Then m = Jx.Prop(msg, "content")
        If m.ValueKind = JsonValueKind.Object Then m = Jx.Prop(m, "content")
        If m.ValueKind = JsonValueKind.String Then Return m.GetString()
        If m.ValueKind = JsonValueKind.Array Then
            Return String.Join(vbLf, m.EnumerateArray().Where(Function(b) b.ValueKind = JsonValueKind.Object AndAlso Jx.StrOf(b, "type", "text") = "text").
                                                         Select(Function(b) Jx.StrOf(b, "text")))
        End If
        Return Nothing
    End Function

    Private Shared Async Function ControlAsync(session As Session, msg As JsonElement, emit As Action(Of Object)) As Task
        Dim action = Jx.StrOf(msg, "action", Jx.StrOf(msg, "subtype"))
        Dim ok = True
        Dim detail As String = Nothing
        Select Case action
            Case "interrupt" : session.Interrupt()
            Case "set_model"
                ok = session.TrySetModel(Jx.StrOf(msg, "model"))
                If Not ok Then detail = "That model is not offered."
            Case "set_permission_mode" : session.State.Mode = AgentState.ParseMode(Jx.StrOf(msg, "mode"), session.State.Mode)
            Case "set_effort" : session.SetEffortLevel(Jx.StrOf(msg, "effort"))
            Case "new_conversation" : session.ResetConversation()
            Case "resume"
                Try
                    ok = Await session.OpenConversationAsync(Jx.StrOf(msg, "session_id"), False).ConfigureAwait(False)
                Catch ex As Exception
                    ok = False
                    detail = ex.Message
                End Try
            Case Else
                ok = False
                detail = $"Unknown control action '{action}'."
        End Select
        emit(New With {.type = "control_response", .action = action, .ok = ok, .error = detail, .model = session.Model,
                       .permissionMode = session.State.ModeLabel, .session_id = session.State.ChatId})
    End Function

End Class

''' <summary>agentic doctor and /doctor: connection, account, models, tools, configuration.</summary>
Friend NotInheritable Class Doctor

    Private Sub New()
    End Sub

    Public Shared Async Function RunAsync(creds As Credentials, state As AgentState, meta As ChatMeta) As Task(Of Integer)
        Dim problems = 0
        Dim ok = Sub(label As String, detail As String) Out.Line("  " & Ansi.Ok("✓") & " " & Ansi.Pad(label, 22) & Ansi.Muted(detail))
        Dim bad = Sub(label As String, detail As String)
                      problems += 1
                      Out.Line("  " & Ansi.Bad("✗") & " " & Ansi.Pad(label, 22) & detail)
                  End Sub
        Dim warn = Sub(label As String, detail As String) Out.Line("  " & Ansi.Warn("!") & " " & Ansi.Pad(label, 22) & Ansi.Muted(detail))
        Out.Line(Ansi.Bold("Agentic CLI doctor") & Ansi.Muted("  v" & AppInfo.Version))
        If creds.IsConfigured() Then ok("Credentials", creds.ServerUrl & " · key " & creds.MaskedKey() & " (" & creds.Source & ")") Else bad("Credentials", "not set — run agentic login (" & Credentials.ConfigFile() & ")")
        If creds.IsConfigured() Then
            Dim sw = Stopwatch.StartNew()
            Try
                Using api As New ApiClient(creds.ServerUrl, creds.ApiKey)
                    Dim health = Await api.GetAsync("/healthz", CancellationToken.None, 15).ConfigureAwait(False)
                    ok("Server", $"reachable in {sw.ElapsedMilliseconds} ms")
                    meta = If(meta, Await New ChatApi(api).MetaAsync(CancellationToken.None).ConfigureAwait(False))
                    ok("API key", If(meta.IsAdmin, "administrator", "user") & " account")
                    If meta.AllModels().Count = 0 Then bad("Models", "none offered" & If(meta.ProviderError Is Nothing, "", ": " & meta.ProviderError)) Else ok("Models", $"{meta.CustomModels.Count} custom, {meta.ProviderModels.Count} provider; default {meta.DefaultModel}")
                    If meta.CanTools Then ok("Server tools", $"web search {If(meta.WebSearch, "on", "off")}, code interpreter {If(meta.CodeInterpreter, "available", "not available")}, {meta.Servers.Count} MCP servers") Else warn("Server tools", "your account lacks ""Chat tools & code"" (local tools still work)")
                    Dim upd As UpdateInfo = Nothing
                    Try
                        upd = Await Updater.CheckAsync(api, CancellationToken.None).ConfigureAwait(False)
                    Catch ex As ApiException
                    End Try
                    Dim auto = Updater.DisabledReason(state?.Settings)
                    If upd Is Nothing Then
                        warn("Updates", "the server publishes no CLI build")
                    ElseIf upd.Available Then
                        warn("Updates", $"{upd.Version} is available ({upd.Reason}) — run agentic update" & If(auto Is Nothing, "", $"; automatic updates: {auto}"))
                    Else
                        ok("Updates", $"up to date" & If(auto Is Nothing, "; automatic", $"; automatic updates: {auto}"))
                    End If
                End Using
            Catch ex As ApiException
                bad("Server", ex.Message)
            End Try
        End If
        For Each tool In {"git", "rg", "node", "python", "dotnet", "pwsh"}
            Dim p = ProjectInfo.Which(tool)
            If p IsNot Nothing Then ok(tool, p) Else If tool = "git" Then warn(tool, "not found (git features and snapshots are off)") Else warn(tool, "not found")
        Next
        Dim shell = If(state?.Shells, New Tools.ShellManager("auto"))
        ok("Bash tool shell", shell.Describe())
        Dim cwd = If(state?.ProjectDir, Environment.CurrentDirectory)
        Dim cfg = If(state?.Settings, Settings.Load(cwd))
        For Each s In cfg.Scopes.Where(Function(x) x.File IsNot Nothing AndAlso File.Exists(x.File))
            Dim parsed As JsonElement
            If Jx.TryParse(Paths.ReadText(s.File), parsed) Then ok("Settings (" & s.Name & ")", s.File) Else bad("Settings (" & s.Name & ")", "invalid JSON: " & s.File)
        Next
        Dim servers = Mcp.McpConfig.Load(cwd, Nothing)
        If servers.Count = 0 Then
            ok("MCP", "no servers configured")
        ElseIf state IsNot Nothing Then
            For Each s In servers
                Dim c = state.Mcp.Get(s.Id)
                If c IsNot Nothing AndAlso c.Status.Connected Then ok("MCP " & s.Id, $"{c.Status.Tools.Count} tools") Else If Not s.Enabled Then warn("MCP " & s.Id, "disabled") Else bad("MCP " & s.Id, If(c?.Status.Error, "not connected"))
            Next
        Else
            ok("MCP", $"{servers.Count} configured: " & String.Join(", ", servers.Select(Function(x) x.Id)))
        End If
        Dim mem = Memory.Load(cwd, True)
        ok("Memory files", If(mem.Count = 0, "none (run /init to create AGENTS.md)", String.Join(", ", mem.Select(Function(f) Paths.Display(f.Path, cwd)))))
        ok("Terminal", $"{Ansi.TermWidth()} columns · colours {If(Ansi.Colors, "on", "off")} · glyphs {If(Ansi.Rich, "unicode", "simple")}")
        Out.Line(If(problems = 0, Ansi.Ok("  All checks passed."), Ansi.Bad($"  {problems} problem(s) found.")))
        Return If(problems = 0, 0, 1)
    End Function

End Class
