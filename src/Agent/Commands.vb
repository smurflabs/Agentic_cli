Option Strict On
Option Explicit On
Option Infer On

Imports System.IO
Imports System.Runtime.InteropServices
Imports System.Text
Imports System.Text.Json.Nodes
Imports System.Text.RegularExpressions
Imports System.Threading
Imports AgenticCli.Api
Imports AgenticCli.Core
Imports AgenticCli.Tools
Imports AgenticCli.Ui

Namespace Agent

    Friend NotInheritable Class CommandInfo
        Public Property Name As String
        Public Property Description As String
        Public Property ArgHint As String
        Public Property TakesArgs As Boolean
        Public Property Source As String = "built-in"
    End Class

    Partial Friend NotInheritable Class Session

        Private Shared ReadOnly BuiltIns As (Name As String, Args As String, Description As String)() = {
            ("help", Nothing, "Show commands and shortcuts"),
            ("clear", "[name]", "Start a new conversation; a name labels the one you leave (aliases: /new, /reset)"),
            ("compact", "[focus]", "Summarize the conversation to free context (optionally: what the summary should focus on)"),
            ("context", Nothing, "Show how full the context window is"),
            ("cost", Nothing, "Show token usage and time for this session"),
            ("usage", Nothing, "Your account's use: tokens today, this month and by model; balance and limits (alias: /stats)"),
            ("find", "<text>", "Find in this conversation: messages, tool output and thinking"),
            ("model", "[model]", "Choose the model"),
            ("effort", "[none|low|medium|high|ultracode]", "Set the reasoning effort (aliases: /think, /reasoning)"),
            ("mode", "[default|acceptEdits|plan|bypass]", "Set the permission mode (Shift+Tab cycles)"),
            ("plan", Nothing, "Turn plan mode on or off"),
            ("goal", "[text|clear]", "Set a goal: keep working and retry failures until it is met"),
            ("claude", "[on|off|new|model|permission|cwd|fallback]", "Use the local Claude Code CLI in chat and configure it"),
            ("permissions", "[add|remove …]", "Show or change allow / ask / deny rules"),
            ("resume", "[id]", "Resume an earlier conversation"),
            ("continue", Nothing, "Resume the most recent conversation in this folder"),
            ("rename", "<title>", "Rename this conversation"),
            ("rewind", Nothing, "Go back to an earlier message: restore code and/or conversation (alias: /undo)"),
            ("branch", "[name]", "Copy the conversation as it is now and continue in the copy (alias: /fork)"),
            ("btw", "<question>", "Ask a side question about this session; the answer is not added to the conversation"),
            ("recap", Nothing, "One line: what this session is about and where it stands"),
            ("diff", "[path]", "Show the changes in the working tree (git)"),
            ("cd", "<path>", "Move this session to another folder, keeping the conversation"),
            ("bookmark", "[on|off]", "Bookmark this conversation (or remove the bookmark)"),
            ("bookmarks", Nothing, "Open one of your bookmarked conversations"),
            ("thoughts", "[n]", "Read what the model thought in the last answer (each thinking phase in full)"),
            ("thinking", "[on|off]", "Thinking on or off (off: the model answers right away, without reasoning)"),
            ("cavemode", "[on|ultra|wenyan|off|status]", "Caveman mode: terse answers that keep every fact and line of code (fewer tokens)"),
            ("limits", "[rounds] [web]", "Max tool-call rounds and web searches per answer (e.g. /limits 40 8; 'default' to clear)"),
            ("parallel", "[on|off|N]", "Parallel agents: split work into subagents that run at the same time (N = most at once)"),
            ("notes", "[on|off]", "Research notes: save findings from web research to research/<chat>.md"),
            ("caveman", "[off|status]", "Caveman mode on (or off)"),
            ("ultracave", Nothing, "Caveman mode, as compressed as it gets"),
            ("megacave", Nothing, "Caveman mode in Classical Chinese (文言文), the tersest"),
            ("agentic", "[edit|add <rule>|remove <n|text>]", "Show or change this project's AGENTIC.md (instructions every message includes)"),
            ("export", "[file|clipboard]", "Export the conversation as Markdown"),
            ("copy", Nothing, "Copy the last answer to the clipboard"),
            ("init", Nothing, "Analyze the codebase and write AGENTS.md"),
            ("memory", "[user|project|local]", "Open a memory file in your editor"),
            ("add-dir", "<path>", "Let the tools work in another folder"),
            ("agents", "[new]", "List or create subagents"),
            ("skills", "[name|new]", "List skills and custom commands"),
            ("mcp", "[reconnect|enable|disable|tools|login|logout|server|catalog|add] [id]", "MCP servers here (also Claude Code's) and on the server; login signs in to hosted ones"),
            ("hooks", Nothing, "Show the configured hooks"),
            ("config", "[set <key> <value>]", "Show or change settings"),
            ("status", Nothing, "Version, connection, model, folders and configuration"),
            ("doctor", Nothing, "Check the setup (alias: /checkup)"),
            ("release-notes", Nothing, "The newest CLI version on the server and what changed"),
            ("statusline", "[command|off]", "Set the command whose output shows in the status line"),
            ("reload-skills", Nothing, "Scan skill and command folders again"),
            ("todos", Nothing, "Show the current todo list"),
            ("bashes", "[kill <id>]", "Background commands (alias: /tasks)"),
            ("monitors", "[stop <#>|all]", "Commands the Monitor tool watches (their output arrives as messages)"),
            ("review", "[PR number]", "Review the current changes or a pull request"),
            ("pr-comments", "[PR number]", "Work through a pull request's review comments"),
            ("ide", "[list|off|<n>]", "Connect to VS Code: your selection goes with messages, edits show as diffs there, IdeDiagnostics reads its problems"),
            ("plugin", "[list|enable|disable|install|uninstall|browse|marketplace …]", "Claude Code plugins: their commands, agents, skills, hooks and MCP servers (alias: /plugins)"),
            ("feedback", "[what happened]", "Send a report to the administrators, with this conversation (alias: /bug)"),
            ("security-review", Nothing, "Security review of the pending changes"),
            ("simplify", Nothing, "Simplify the pending changes without changing what they do"),
            ("verify", Nothing, "Build, test and run the project to check that the latest changes work"),
            ("research", "<question>", "Deep research on the server: several web searches, sources read and cross-checked, a cited report"),
            ("worktree", "[name|list|done|remove <name>]", "Work in a git worktree: its own folder and branch (like --worktree)"),
            ("schedule", "[<when> <prompt>|list|all|pause|resume|delete|run <#>]", "Prompts the server sends to this conversation on a schedule, also while the CLI is closed"),
            ("run", "[name|#|stop|status|log|open|share|edit]", "Run or test this project (a dev server, the app, its tests) or share its website as a link"),
            ("read", "[on|off]", "Read the last answer aloud in your voice (on: every answer; Esc stops it)"),
            ("tldr", Nothing, "Write and speak a short spoken summary of the last answer"),
            ("voice", "[name]", "The voice that reads answers aloud and talks in Dictation mode"),
            ("dictation", "[on|off]", "Dictation mode: talk instead of typing; each answer's short summary is spoken (alias: /talk)"),
            ("web", "[on|off]", "Web search on the server"),
            ("code", "[on|off]", "The server's code interpreter"),
            ("output-style", "[default|explanatory|learning|concise]", "How answers are written"),
            ("verbose", Nothing, "Show full tool output and thinking (Ctrl+O)"),
            ("vim", Nothing, "Vim keys in the message box (Esc: normal mode, i/a: insert)"),
            ("keys", "[list|set <action> <keys>|reset <action>|all]", "See and change every key (they stay after a restart; aliases: /keybindings, /hotkeys)"),
            ("loop", "[interval] <prompt> | stop [#]", "Run a prompt or /command every few minutes while idle (default 10m)"),
            ("heartbeat", "[on [interval]|off|now]", "Check HEARTBEAT.md on a schedule; quiet when nothing needs attention"),
            ("share", "[off]", "Make a read-only link to this conversation"),
            ("memories", "[on|off|clear]", "What the assistant remembers about you (all your chats)"),
            ("remember", "<fact>", "Add something the assistant should know about you"),
            ("forget", "<id or words>", "Remove a memory"),
            ("prompts", Nothing, "Your saved prompts from the web app (they work as /commands)"),
            ("login", Nothing, "Change the server or API key"),
            ("logout", Nothing, "Forget the API key and exit"),
            ("version", Nothing, "Show the version"),
            ("exit", Nothing, "Exit (alias: /quit)")}

        Public Function CommandCatalog() As List(Of CommandInfo)
            Dim list = BuiltIns.Select(Function(b) New CommandInfo With {.Name = b.Name, .ArgHint = b.Args, .TakesArgs = b.Args IsNot Nothing, .Description = b.Description}).ToList()
            ' Descriptions that name a key show the key set in /keys.
            For Each c In list
                If c.Name = "mode" Then c.Description = $"Set the permission mode ({KeyBindings.Describe("cycle-mode")} cycles)"
                If c.Name = "verbose" Then c.Description = $"Show full tool output and thinking ({KeyBindings.Describe("verbose")})"
            Next
            For Each s In State.Skills.All()
                If list.Any(Function(c) c.Name.Equals(s.Name, StringComparison.OrdinalIgnoreCase)) Then Continue For
                list.Add(New CommandInfo With {.Name = s.Name, .ArgHint = s.ArgumentHint, .TakesArgs = True,
                                               .Description = If(s.Kind = "skill", "(skill) ", "") & ToolKit.OneLine(s.Description, 90) & Ansi.Dim2(" · " & s.Source), .Source = s.Source})
            Next
            For Each p In _savedPrompts
                If list.Any(Function(c) c.Name.Equals(p.Command, StringComparison.OrdinalIgnoreCase)) Then Continue For
                list.Add(New CommandInfo With {.Name = p.Command, .TakesArgs = True, .ArgHint = "[text]", .Source = "saved prompt",
                                               .Description = ToolKit.OneLine(If(String.IsNullOrWhiteSpace(p.Title), p.Content, p.Title), 90) & Ansi.Dim2(" · saved prompt")})
            Next
            Return list
        End Function

        ''' <summary>Runs a slash command; custom commands and skills become a message.</summary>
        Private Async Function CommandAsync(line As String) As Task
            Dim m = Regex.Match(line, "^/(\S+)\s*(.*)$", RegexOptions.Singleline)
            Dim cmd = m.Groups(1).Value.ToLowerInvariant()
            Dim rest = m.Groups(2).Value.Trim()
            Select Case cmd
                Case "help", "?" : ShowHelp()
                Case "clear", "new", "reset"
                    ' A name labels the conversation being left (to find it in /resume).
                    If rest.Length > 0 AndAlso Not String.IsNullOrEmpty(State.ChatId) Then
                        Try
                            Await _chat.UpdateAsync(State.ChatId, CancellationToken.None, title:=rest).ConfigureAwait(False)
                        Catch ex As ApiException
                            Out.Warning("Could not name the previous conversation: " & ex.Message)
                        End Try
                    End If
                    NewConversation()
                Case "btw" : Await BtwAsync(rest).ConfigureAwait(False)
                Case "recap" : Await RecapAsync().ConfigureAwait(False)
                Case "branch", "fork" : Await BranchAsync(rest).ConfigureAwait(False)
                Case "diff" : Await DiffAsync(rest).ConfigureAwait(False)
                Case "cd" : ChangeDir(rest)
                Case "release-notes" : Await ReleaseNotesAsync().ConfigureAwait(False)
                Case "statusline" : StatusLineCommand(rest)
                Case "reload-skills" : ReloadSkills()
                Case "compact" : Await CompactAsync(rest).ConfigureAwait(False)
                Case "context" : Await ShowContextAsync().ConfigureAwait(False)
                Case "cost" : ShowCost()
                Case "usage", "stats" : Await UsageCommandAsync().ConfigureAwait(False)
                Case "find" : Await FindCommandAsync(rest).ConfigureAwait(False)
                Case "pr-comments" : Await SendAsync(PrCommentsPrompt(rest)).ConfigureAwait(False)
                Case "ide" : Await IdeCommandAsync(rest).ConfigureAwait(False)
                Case "plugin", "plugins" : Await PluginCommandAsync(rest).ConfigureAwait(False)
                Case "feedback", "bug" : Await FeedbackCommandAsync(rest).ConfigureAwait(False)
                Case "model" : Await ChooseModelAsync(rest).ConfigureAwait(False)
                Case "effort", "think", "reasoning" : SetEffort(rest)
                Case "thinking" : SetThinking(rest)
                Case "cavemode" : SetCaveman(rest)
                Case "limits" : SetLimits(rest)
                Case "parallel" : SetParallel(rest)
                Case "notes" : SetResearchNotes(rest)
                Case "caveman" : SetCaveman(If(String.IsNullOrWhiteSpace(rest), "on", rest))
                Case "ultracave" : SetCaveman("ultra")
                Case "megacave" : SetCaveman("wenyan")
                Case "bookmark" : Await BookmarkAsync(rest).ConfigureAwait(False)
                Case "bookmarks" : Await BookmarksPickerAsync().ConfigureAwait(False)
                Case "thoughts" : ShowThoughts(rest)
                Case "agentic" : AgenticMd(rest)
                Case "mode" : SetMode(rest)
                Case "goal" : Await GoalCommandAsync(rest).ConfigureAwait(False)
                Case "claude" : Await ClaudeCommandAsync(rest).ConfigureAwait(False)
                Case "plan"
                    State.Mode = If(State.Mode = PermissionMode.Plan, PermissionMode.Default, PermissionMode.Plan)
                    Out.Success("Plan mode " & If(State.Mode = PermissionMode.Plan, "on: Agentic researches and proposes a plan before changing anything", "off"))
                    If rest.Length > 0 Then Await SendAsync(rest).ConfigureAwait(False)
                Case "permissions", "allowed-tools" : Permissions(rest)
                Case "resume" : If rest.Length > 0 Then Await OpenConversationAsync(rest, True).ConfigureAwait(False) Else Await ResumePickerAsync().ConfigureAwait(False)
                Case "continue"
                    Dim latest = Sessions.List(State.ProjectDir).FirstOrDefault(Function(s) s.ChatId <> State.ChatId)
                    If latest Is Nothing Then Out.Note("No earlier conversation in this folder.") Else Await OpenConversationAsync(latest.ChatId, True).ConfigureAwait(False)
                Case "rename" : Await RenameAsync(rest).ConfigureAwait(False)
                Case "rewind", "undo", "checkpoint" : Await RewindMenuAsync().ConfigureAwait(False)
                Case "export" : Await ExportAsync(rest).ConfigureAwait(False)
                Case "copy"
                    If _lastAnswer Is Nothing Then Out.Note("No answer to copy yet.") Else If Clipboard.SetText(_lastAnswer) Then Out.Success("Copied the last answer to the clipboard.") Else Out.Fail("Could not use the clipboard.")
                Case "init" : Await SendAsync(InitPrompt).ConfigureAwait(False)
                Case "memory" : OpenMemory(rest)
                Case "add-dir" : AddDir(rest)
                Case "agents" : Agents(rest)
                Case "skills" : SkillsCommand(rest)
                Case "mcp" : Await McpCommandAsync(rest).ConfigureAwait(False)
                Case "hooks" : ShowHooks()
                Case "config", "settings" : ConfigCommand(rest)
                Case "status" : ShowStatus()
                Case "doctor", "checkup" : Await Doctor.RunAsync(_creds, State, _meta).ConfigureAwait(False)
                Case "todos" : Out.Write(TodoWriteTool.Render(State.Todos, "  "))
                Case "bashes", "shells", "tasks" : Bashes(rest)
                Case "review", "code-review" : Await SendAsync(ReviewPrompt(rest)).ConfigureAwait(False)
                Case "security-review" : Await SendAsync(SecurityReviewPrompt).ConfigureAwait(False)
                Case "simplify" : Await SendAsync(SimplifyPrompt).ConfigureAwait(False)
                Case "verify" : Await SendAsync(VerifyPrompt).ConfigureAwait(False)
                Case "research" : Await ResearchAsync(rest).ConfigureAwait(False)
                Case "worktree", "worktrees" : WorktreeCommand(rest)
                Case "monitors", "monitor" : MonitorsCommand(rest)
                Case "schedule", "schedules" : Await ScheduleCommandAsync(rest).ConfigureAwait(False)
                Case "run", "runs", "preview" : Await RunCommandAsync(rest).ConfigureAwait(False)
                Case "read", "readaloud", "read-aloud" : Await ReadCommandAsync(rest).ConfigureAwait(False)
                Case "tldr" : Await TldrAsync().ConfigureAwait(False)
                Case "voice", "voices" : Await VoiceCommandAsync(rest).ConfigureAwait(False)
                Case "dictation", "dictate", "talk" : Await DictationCommandAsync(rest).ConfigureAwait(False)
                Case "web" : Toggle("webSearch", rest, _meta.WebSearch, Sub(v) _server.WebSearch = v, Function() _server.WebSearch, "Web search")
                Case "code" : Toggle("codeInterpreter", rest, _meta.CodeInterpreter, Sub(v) _server.CodeInterpreter = v, Function() _server.CodeInterpreter, "Code interpreter")
                Case "output-style" : SetStyle(rest)
                Case "vim" : ToggleVim()
                Case "keys", "keybindings", "hotkeys", "shortcuts" : KeysCommand(rest)
                Case "loop", "loops" : LoopCommand(rest)
                Case "heartbeat" : Await HeartbeatCommandAsync(rest).ConfigureAwait(False)
                Case "share" : Await ShareAsync(rest).ConfigureAwait(False)
                Case "memories" : Await MemoriesCommandAsync(rest).ConfigureAwait(False)
                Case "remember" : Await RememberOnServerAsync(rest).ConfigureAwait(False)
                Case "forget" : Await ForgetAsync(rest).ConfigureAwait(False)
                Case "prompts" : Await PromptsCommandAsync(rest).ConfigureAwait(False)
                Case "verbose"
                    State.Verbose = Not State.Verbose
                    State.ShowThinking = State.Verbose
                    Out.Success("Verbose " & If(State.Verbose, "on", "off"))
                Case "login", "setup"
                    Dim c = Await Program.LoginAsync(_creds).ConfigureAwait(False)
                    If c IsNot Nothing Then
                        Out.Note("Restart agentic to use the new connection.")
                        _quit = True
                    End If
                Case "logout"
                    Credentials.Forget()
                    Out.Success("The API key was removed from this computer.")
                    _quit = True
                Case "version" : Out.Line(AppInfo.Name & " " & AppInfo.Version)
                Case "exit", "quit", "q" : _quit = True
                Case Else
                    ' An MCP server's prompt: /mcp__server__prompt arguments… (the last argument takes the rest).
                    If cmd.StartsWith("mcp__", StringComparison.Ordinal) Then
                        For Each c In State.Mcp.ConnectedServers()
                            For Each p In c.Prompts
                                If McpTool.ToolName(c.Options.Id, p.Name) <> cmd Then Continue For
                                Dim words = rest.Split({" "c}, StringSplitOptions.RemoveEmptyEntries)
                                Dim given As New Dictionary(Of String, String)
                                For i = 0 To Math.Min(words.Length, p.Args.Count) - 1
                                    given(p.Args(i).Name) = If(i = p.Args.Count - 1, String.Join(" ", words.Skip(i)), words(i))
                                Next
                                Dim missing = p.Args.Where(Function(x) x.Required AndAlso Not given.ContainsKey(x.Name)).Select(Function(x) x.Name).ToList()
                                If missing.Count > 0 Then
                                    Out.Fail($"/{cmd} needs: {String.Join(" ", missing.Select(Function(x) "<" & x & ">"))}")
                                    Return
                                End If
                                Await SendAsync(Await c.GetPromptAsync(p.Name, given).ConfigureAwait(False)).ConfigureAwait(False)
                                Return
                            Next
                        Next
                    End If
                    Dim custom = State.Skills.FindByName(cmd)
                    If custom Is Nothing Then
                        ' A prompt saved in the web app: its text, then what was typed after the command.
                        Dim saved = FindSavedPrompt(cmd)
                        If saved IsNot Nothing Then
                            Await SendAsync(saved.Content & If(rest.Length > 0, vbLf & vbLf & rest, "")).ConfigureAwait(False)
                            Return
                        End If
                        Out.Fail($"Unknown command /{cmd}. Type /help for the list.")
                        Return
                    End If
                    Await SendAsync(Await ExpandCustomAsync(custom, rest).ConfigureAwait(False)).ConfigureAwait(False)
                    Return
            End Select
            Out.Line()
        End Function

        ''' <summary>A custom command's prompt ($ARGUMENTS, !`command` output, @file references) or a skill invoked by name.</summary>
        Private Async Function ExpandCustomAsync(s As Skills.SkillInfo, args As String) As Task(Of String)
            If s.Kind = "skill" Then
                Return $"<command-name>/{s.Name}</command-name>" & vbLf & "Use this skill for the request below." & vbLf & $"<skill name=""{s.Name}"">" & vbLf & s.Body & vbLf & "</skill>" &
                       If(args.Length > 0, vbLf & vbLf & args, "")
            End If
            Dim text = Skills.SkillStore.Expand(s, args)
            ' !`cmd` runs a command and puts its output in place (Claude Code command files).
            Dim matches = Regex.Matches(text, "!`([^`]+)`")
            For Each mm As Match In matches.Cast(Of Match)().Reverse()
                Dim r = Await State.Shells.RunAsync(mm.Groups(1).Value, State.Cwd, 60000, CancellationToken.None, stopAtWait:=True).ConfigureAwait(False)
                text = text.Substring(0, mm.Index) & r.Output & text.Substring(mm.Index + mm.Length)
            Next
            Return $"<command-name>/{s.Name}</command-name>" & vbLf & text
        End Function

        Private Sub ShowHelp()
            Out.Line(Ansi.Bold("Commands"))
            For Each c In CommandCatalog()
                Out.Line("  " & Ansi.Accent(Ansi.Pad("/" & c.Name & If(c.ArgHint Is Nothing, "", " " & c.ArgHint), 34)) & " " & Ansi.Muted(c.Description))
            Next
            Out.Line()
            Out.Line(Ansi.Bold("In the message box"))
            Dim d = Function(id As String) KeyBindings.Describe(id)
            Dim keys = {("@path", "Attach a file or folder (Tab completes)"), ("! cmd", "Run a command here; its output goes with your next message"),
                        ("# note", "Save a note to a memory file (AGENTS.md)"), ("\ + Enter, " & d("newline"), "New line (pasting keeps line breaks)"),
                        (d("submit"), "Send"), (d("cycle-mode"), "Cycle normal → accept edits → plan mode"), (d("stop"), "Stop the answer"),
                        (d("clear"), "Clear the box"), (d("rewind"), "Go back to an earlier message (in an empty box)"),
                        (d("up") & " / " & d("down") & ", " & d("history-search"), "History / search history"), (d("verbose"), "Verbose: full tool output and thinking"),
                        (d("todos"), "Show the todo list"), (d("undo"), "Undo in the box"), (d("clear-screen"), "Clear the screen"),
                        (d("interrupt") & " twice, " & d("exit"), "Exit")}
            For Each k In keys
                Out.Line("  " & Ansi.Accent(Ansi.Pad(k.Item1, 26)) & " " & Ansi.Muted(k.Item2))
            Next
            Out.Line(Ansi.Muted("  Every key (and many more: moving, deleting, menus) can be changed with /keys."))
        End Sub

        Private Sub NewConversation()
            State.ChatId = Nothing
            State.Checkpoints = Nothing
            State.Todos = New List(Of TodoItem)
            State.ReadFiles.Clear()
            _title = Nothing
            _ctx = Nothing
            _lastAnswer = Nothing
            _gitReady = Task.Run(Sub() _gitSnapshot = State.Project.GitSnapshot())
            ' SessionStart again, like Claude Code after /clear (matcher "clear"): its output is the new conversation's context.
            _hookContext = State.Hooks.Run("SessionStart", "clear", New Dictionary(Of String, Object) From {{"source", "clear"}}).Context
            If State.Interactive Then Out.Raw(Ansi.E & "[2J" & Ansi.E & "[3J" & Ansi.E & "[H")
            Banner()
            Out.Success("New conversation")
        End Sub

        Private Async Function CompactAsync(focus As String) As Task
            If String.IsNullOrEmpty(State.ChatId) Then
                Out.Note("Nothing to compact yet.")
                Return
            End If
            State.Hooks.Run("PreCompact", "manual", New Dictionary(Of String, Object) From {{"trigger", "manual"}, {"custom_instructions", focus}})
            Out.StartSpinner("Compacting the conversation…")
            Try
                Dim r = Await _chat.CompactAsync(State.ChatId, CancellationToken.None, focus).ConfigureAwait(False)
                Out.StopSpinner()
                If r.Context IsNot Nothing Then _ctx = r.Context
                If r.Ok Then
                    If r.Stats IsNot Nothing Then
                        Out.Success(r.Stats.Line())
                    Else
                        Out.Success("Compacted: older messages are now a summary" & If(r.Context Is Nothing, ".", $" ({r.Context.Percent:0}% of the context used)."))
                    End If
                    State.Hooks.Run("PostCompact", "manual", New Dictionary(Of String, Object) From {{"trigger", "manual"}, {"custom_instructions", focus}})
                    Dim again = State.Hooks.Run("SessionStart", "compact", New Dictionary(Of String, Object) From {{"source", "compact"}})
                    If again.Context IsNot Nothing Then _hookContext = again.Context
                Else
                    Out.Note(If(String.IsNullOrWhiteSpace(r.Message), "Nothing to compact.", r.Message))
                End If
            Catch ex As ApiException
                Out.StopSpinner()
                Out.Fail(ex.Message)
            End Try
        End Function

        Private Async Function ShowContextAsync() As Task
            Dim ctx = _ctx
            If Not String.IsNullOrEmpty(State.ChatId) Then
                Try
                    ctx = Await _chat.ContextAsync(State.ChatId, _model, CancellationToken.None).ConfigureAwait(False)
                    _ctx = ctx
                Catch ex As ApiException
                End Try
            End If
            Dim max = If(ctx?.Max, _meta.WindowFor(_model))
            Dim instr = BuildInstructions().Length \ 4
            Dim tools = BuildTools().Sum(Function(t) (t.Description.Length + t.Parameters().ToJsonString().Length) \ 4)
            Dim mem = Memory.Combined(Memory.Load(State.ProjectDir, _includeClaude)).Length \ 4
            Dim used = If(ctx?.Used, 0)
            Dim messages = Math.Max(0, used - instr - tools)
            Out.Line(Ansi.Bold("Context usage") & Ansi.Muted($"  {_meta.Find(_model)?.Name} · {TurnRunner.FormatTokens(Math.Max(used, instr + tools))}/{TurnRunner.FormatTokens(max)} tokens"))
            Dim cells = 50
            Dim bar As New StringBuilder()
            Dim parts = {(Name:="Instructions (incl. memory)", Tokens:=instr, Color:=141), (Name:="Tools", Tokens:=tools, Color:=75), (Name:="Messages", Tokens:=messages, Color:=114)}
            Dim filled = 0
            For Each p In parts
                Dim n = CInt(Math.Round(cells * p.Tokens / Math.Max(1, max)))
                n = Math.Min(n, cells - filled)
                bar.Append(Ansi.Fg(p.Color, New String("█"c, n)))
                filled += n
            Next
            bar.Append(Ansi.Dim2(New String("░"c, cells - filled)))
            Out.Line("  " & bar.ToString() & "  " & Ansi.Muted($"{100.0 * Math.Max(used, instr + tools) / Math.Max(1, max):0.0}%"))
            For Each p In parts
                Out.Line("  " & Ansi.Fg(p.Color, "█") & " " & Ansi.Pad(p.Name, 30) & Ansi.Muted(TurnRunner.FormatTokens(p.Tokens) & " tokens"))
            Next
            Out.Line(Ansi.Muted($"  of which memory files: {TurnRunner.FormatTokens(mem)} tokens"))
            If ctx IsNot Nothing Then
                Out.Line(Ansi.Muted($"  {ctx.MessagesInFull} of {ctx.Messages} messages in full" & If(ctx.Compacted, ", older ones summarized", "") & $"; auto-compact at {ctx.AutoCompactPercent}%"))
            End If
        End Function

        Private Sub ShowCost()
            Dim wall = DateTime.UtcNow - _startedAt
            Out.Line(Ansi.Bold("Session usage"))
            Out.Line($"  Total tokens:     {TurnRunner.FormatTokens(_promptTokens + _completionTokens)} ({TurnRunner.FormatTokens(_promptTokens)} input, {TurnRunner.FormatTokens(_completionTokens)} output)")
            Out.Line($"  Messages:         {_turns} · tool uses: {_toolUses}")
            Out.Line($"  Answer time:      {TimeSpan.FromMilliseconds(_apiMs):h\:mm\:ss}")
            Out.Line($"  Session time:     {wall:h\:mm\:ss}")
            Out.Note("  Costs are tracked by your Agentic server (Finance in the web app).")
        End Sub

        Private Async Function ChooseModelAsync(arg As String) As Task
            Dim chosen As ModelInfo = Nothing
            ' Claude Code runs the local claude CLI instead of a server model.
            If IsClaudeModel(arg) AndAlso ClaudeAvailable() Then
                _model = ClaudeModelId
                Await _chat.SetLastModelAsync(_model).ConfigureAwait(False)
                Out.Success("Model: Claude Code (local). Messages now run the claude CLI; it keeps its own session.")
                Return
            End If
            ' An exact id or name picks it at once; anything else opens the list filtered by it.
            If arg.Length > 0 Then chosen = _meta.Find(arg)
            If chosen Is Nothing Then
                Dim models = _meta.CustomModels.Concat(_meta.ProviderModels.OrderBy(Function(x) Vendor(x.Id), StringComparer.OrdinalIgnoreCase).
                                                                            ThenBy(Function(x) x.Id, StringComparer.OrdinalIgnoreCase)).ToList()
                If models.Count = 0 Then
                    Out.Fail("The server offers no models" & If(_meta.ProviderError Is Nothing, ".", ": " & _meta.ProviderError))
                    Return
                End If
                Dim options = models.Select(Function(x) New MenuOption(
                    If(x.Custom OrElse x.OwnedBy = "you", x.Name, x.Id) & If(x.Id = _model, " " & Ansi.Ok("✓"), ""),
                    If(x.Custom, "custom · " & If(x.Routes.Count > 0, String.Join(", ", x.Routes), ToolKit.OneLine(x.Description, 80)),
                       If(x.OwnedBy = "you", "your own model (My models) · free · " & x.Id,
                                 Vendor(x.Id) & If(Not String.IsNullOrWhiteSpace(x.Name) AndAlso x.Name <> x.Id, " · " & x.Name, ""))),
                    x.Id)).ToList()
                ' Claude Code (the local claude CLI) is offered first when it is installed.
                Dim hasClaude = ClaudeAvailable()
                If hasClaude Then options.Insert(0, New MenuOption("Claude Code (local)" & If(IsClaudeModel(_model), " " & Ansi.Ok("✓"), ""), "runs the claude CLI on this computer · keeps its own session", ClaudeModelId))
                Dim current = Math.Max(0, models.FindIndex(Function(x) x.Id = _model)) + If(hasClaude, 1, 0)
                If hasClaude AndAlso IsClaudeModel(_model) Then current = 0
                Dim i = Menu.Select(" Select a model" & Ansi.Muted($"  ({_meta.CustomModels.Count} custom, {_meta.ProviderModels.Count} from the provider)"),
                                    options, current, filterable:=True, initialQuery:=arg)
                If i < 0 Then Return
                If hasClaude AndAlso i = 0 Then
                    _model = ClaudeModelId
                    Await _chat.SetLastModelAsync(_model).ConfigureAwait(False)
                    Out.Success("Model: Claude Code (local). Messages now run the claude CLI; it keeps its own session.")
                    Return
                End If
                chosen = models(i - If(hasClaude, 1, 0))
            End If
            If chosen Is Nothing Then Return
            _model = chosen.Id
            ' The server remembers it for the next session, here and in the web and desktop apps.
            If Not String.IsNullOrEmpty(State.ChatId) Then
                Try
                    Await _chat.UpdateAsync(State.ChatId, CancellationToken.None, model:=_model).ConfigureAwait(False)
                Catch ex As ApiException
                End Try
            Else
                Await _chat.SetLastModelAsync(_model).ConfigureAwait(False)
            End If
            Out.Success("Model: " & If(chosen.Name, chosen.Id))
        End Function

        ''' <summary>The provider part of a model id ("dddai2/deepseek-v4" → "dddai2").</summary>
        Private Shared Function Vendor(id As String) As String
            Dim slash = If(id, "").IndexOf("/"c)
            Return If(slash > 0, id.Substring(0, slash), "models")
        End Function

        Private Sub SetEffort(arg As String)
            If arg.Length = 0 Then
                Dim levels = {"none", "low", "medium", "high", "ultracode"}
                Dim hints = {"Answer right away", "Quick thinking", "Balanced (default)", "Think longer about hard problems", "Deepest reasoning; complete, careful code"}
                Dim i = Menu.Select(" Reasoning effort", levels.Select(Function(l, n) New MenuOption(l, hints(n))).ToList(), Array.IndexOf(levels, _server.ReasoningEffort))
                If i < 0 Then Return
                arg = levels(i)
            End If
            _server.ReasoningEffort = NormalizeEffort(arg)
            Out.Success("Reasoning effort: " & _server.ReasoningEffort)
        End Sub

        ''' <summary>/thoughts [n]: the last answer's thinking, every phase in full (or only phase n).</summary>
        Private Sub ShowThoughts(arg As String)
            If _lastThoughts.Count = 0 Then
                Out.Note("The last answer has no thinking to show.")
                Return
            End If
            Dim pick As Integer
            Dim only = If(Integer.TryParse(If(arg, "").Trim(), pick) AndAlso pick >= 1 AndAlso pick <= _lastThoughts.Count, pick, 0)
            For i = 0 To _lastThoughts.Count - 1
                If only > 0 AndAlso i + 1 <> only Then Continue For
                Out.Line(Ansi.Muted($"{Ansi.Therefore} Thought {i + 1} of {_lastThoughts.Count}"))
                Dim w = Math.Max(30, Math.Min(Ansi.TermWidth() - 1, 120))
                For Each para In _lastThoughts(i).Replace(ChrW(13), "").Split(ChrW(10))
                    For Each l In Ansi.WrapText(para, w, "  ", "  ")
                        Out.Line(Ansi.Muted(Ansi.Italic(l)))
                    Next
                Next
                Out.Line()
            Next
        End Sub

        ''' <summary>/limits [rounds] [web]: tool-call rounds and web searches per answer for this chat. "default" or "off" clears.</summary>
        Private Sub SetLimits(arg As String)
            Dim parts = If(arg, "").Split({" "c}, StringSplitOptions.RemoveEmptyEntries)
            If parts.Length = 0 Then
                Out.Note($"Tool-call rounds: {If(_server.MaxToolRounds.HasValue, _server.MaxToolRounds.Value.ToString(), "server default")}. " &
                         $"Web searches: {If(_server.MaxWebSearches.HasValue, _server.MaxWebSearches.Value.ToString(), "server default")}. Set with /limits <rounds> [web], or /limits default.")
                Return
            End If
            If parts.Length = 1 AndAlso (parts(0).Equals("default", StringComparison.OrdinalIgnoreCase) OrElse parts(0).Equals("clear", StringComparison.OrdinalIgnoreCase) OrElse parts(0).Equals("off", StringComparison.OrdinalIgnoreCase)) Then
                _server.MaxToolRounds = Nothing : _server.MaxWebSearches = Nothing
                Out.Success("Tool limits cleared: the server defaults apply.")
                Return
            End If
            Dim rounds As Integer
            If Not Integer.TryParse(parts(0), rounds) OrElse rounds < 0 OrElse rounds > 1000 Then
                Out.Warning("Usage: /limits <rounds 0-1000> [web searches 0-50], or /limits default")
                Return
            End If
            _server.MaxToolRounds = rounds
            If parts.Length > 1 Then
                Dim web As Integer
                If Integer.TryParse(parts(1), web) AndAlso web >= 0 AndAlso web <= 50 Then _server.MaxWebSearches = web
            End If
            Out.Success($"Tool limits for this chat: {rounds} tool-call round{If(rounds = 1, "", "s")}" &
                        If(_server.MaxWebSearches.HasValue, $", {_server.MaxWebSearches.Value} web search{If(_server.MaxWebSearches.Value = 1, "", "es")}", "") & If(rounds = 0, " (tools off)", "") & " per answer.")
        End Sub

        ''' <summary>
        ''' /parallel on | off | N: parallel agents. On, the model is told to split independent work into Task subagents started
        ''' together, and a round's subagents and reads run at the same time (N caps the subagents at once). Remembered (user settings).
        ''' </summary>
        Private Sub SetParallel(arg As String)
            Dim a = If(arg, "").Trim().ToLowerInvariant()
            If a.Length = 0 OrElse a = "status" Then
                Out.Note(If(_runner.Parallel, $"Parallel agents: on (up to {_runner.MaxParallelAgents} subagents and {_runner.MaxParallelReads} reads at once).", "Parallel agents: off (one step at a time).") &
                         " Change with /parallel on, off, or a number.")
                Return
            End If
            Dim n As Integer
            If a = "on" OrElse a = "yes" Then
                _runner.Parallel = True
            ElseIf a = "off" OrElse a = "no" Then
                _runner.Parallel = False
            ElseIf Integer.TryParse(a, n) AndAlso n >= 1 AndAlso n <= 16 Then
                _runner.Parallel = True
                _runner.MaxParallelAgents = n
                State.Settings.SetValue("user", "maxParallelAgents", System.Text.Json.Nodes.JsonValue.Create(n))
            Else
                Out.Warning("Usage: /parallel on | off | <subagents at once, 1-16>")
                Return
            End If
            State.Settings.SetValue("user", "parallelAgents", System.Text.Json.Nodes.JsonValue.Create(_runner.Parallel))
            _server.Parallel = If(_runner.Parallel, CType(Nothing, Boolean?), False)
            _server.MaxParallelAgents = If(_runner.Parallel, CType(_runner.MaxParallelAgents, Integer?), Nothing)
            Out.Success(If(_runner.Parallel, $"Parallel agents on: up to {_runner.MaxParallelAgents} subagents at once.", "Parallel agents off: one step at a time."))
        End Sub

        ''' <summary>/notes on | off: the server saves what web research found to research/&lt;chat&gt;.md in this folder (user setting "researchNotes").</summary>
        Private Sub SetResearchNotes(arg As String)
            Dim a = If(arg, "").Trim().ToLowerInvariant()
            Dim isOn = Not (_server.ResearchNotes.HasValue AndAlso Not _server.ResearchNotes.Value)
            If a.Length = 0 OrElse a = "status" Then
                Out.Note($"Research notes: {If(isOn, "on (findings from web research go to research/<chat>.md)", "off")}. Change with /notes on or off.")
                Return
            End If
            If a = "on" OrElse a = "yes" Then
                isOn = True
            ElseIf a = "off" OrElse a = "no" Then
                isOn = False
            Else
                Out.Warning("Usage: /notes on | off")
                Return
            End If
            State.Settings.SetValue("user", "researchNotes", System.Text.Json.Nodes.JsonValue.Create(isOn))
            _server.ResearchNotes = If(isOn, CType(Nothing, Boolean?), False)
            Out.Success(If(isOn, "Research notes on: findings from web research go to research/<chat>.md.", "Research notes off."))
        End Sub

        Private Shared ReadOnly CaveModes As New Dictionary(Of String, String) From {{"full", "Caveman"}, {"ultra", "Ultracave"}, {"wenyan", "Megacave (文言文)"}}

        ''' <summary>
        ''' /cavemode on | ultra | wenyan | off | status: terse answers that keep every fact, code, command, path and error
        ''' exactly (the server adds the rules to every reply). Remembered for the next sessions (user setting "cavemode").
        ''' </summary>
        Friend Sub SetCaveman(arg As String)
            Dim a = If(arg, "").Trim().ToLowerInvariant()
            If a = "status" OrElse a = "?" Then
                Out.Note(If(_server.Caveman Is Nothing, "Caveman mode is off. /cavemode on, ultra or wenyan turns it on.", $"Caveman mode: {CaveModes(_server.Caveman)}"))
                Return
            End If
            Dim mode As String
            Select Case a
                Case "" : mode = If(_server.Caveman Is Nothing, "full", Nothing)
                Case "on", "full", "caveman" : mode = "full"
                Case "ultra", "ultracave" : mode = "ultra"
                Case "wenyan", "megacave" : mode = "wenyan"
                Case "off", "normal", "stop" : mode = Nothing
                Case Else
                    Out.Warning("Use /cavemode on | ultra | wenyan | off | status")
                    Return
            End Select
            _server.Caveman = mode
            Try
                State.Settings.SetValue("user", "cavemode", If(mode Is Nothing, Nothing, System.Text.Json.Nodes.JsonValue.Create(mode)))
            Catch ex As Exception When TypeOf ex Is IO.IOException OrElse TypeOf ex Is UnauthorizedAccessException
            End Try
            Out.Success(If(mode Is Nothing, "Caveman mode off: normal answers.", $"{CaveModes(mode)} mode on: terse answers, code and facts kept exactly."))
        End Sub

        Private Sub SetThinking(arg As String)
            Dim a = If(arg, "").Trim().ToLowerInvariant()
            Dim wantOn = If(a = "on", True, If(a = "off", False, _server.Thinking.HasValue AndAlso Not _server.Thinking.Value))
            _server.Thinking = If(wantOn, CType(Nothing, Boolean?), False)
            Out.Success(If(wantOn, $"Thinking on (reasoning effort {_server.ReasoningEffort})", "Thinking off: the model answers right away, without reasoning"))
        End Sub

        Private Async Function BookmarkAsync(arg As String) As Task
            If String.IsNullOrEmpty(State.ChatId) Then
                Out.Note("Nothing to bookmark yet: send a message first.")
                Return
            End If
            Dim a = If(arg, "").Trim().ToLowerInvariant()
            Dim want As Boolean
            If a = "on" OrElse a = "off" Then
                want = a = "on"
            Else
                Dim mine = Await _chat.ListAsync(CancellationToken.None, limit:=500, bookmarkedOnly:=True).ConfigureAwait(False)
                want = Not mine.Any(Function(c) c.Id = State.ChatId)
            End If
            Await _chat.UpdateAsync(State.ChatId, CancellationToken.None, bookmarked:=want).ConfigureAwait(False)
            Out.Success(If(want, "Bookmarked. /bookmarks lists your bookmarked conversations.", "Bookmark removed."))
        End Function

        Private Async Function BookmarksPickerAsync() As Task
            Dim list = Await _chat.ListAsync(CancellationToken.None, limit:=100, bookmarkedOnly:=True).ConfigureAwait(False)
            If list.Count = 0 Then
                Out.Note("No bookmarked conversations yet. /bookmark bookmarks the current one.")
                Return
            End If
            Dim i = Menu.Select(" Bookmarked conversations", list.Select(Function(c) New MenuOption(c.Title, $"{If(c.UpdatedUtc.HasValue, Ago(c.UpdatedUtc.Value), "")} · {ToolKit.OneLine(c.Preview, 70)}", c.Id)).ToList())
            If i < 0 Then Return
            Await OpenConversationAsync(list(i).Id, True).ConfigureAwait(False)
        End Function

        ''' <summary>This project's instructions file: an existing AGENTIC.md / agentic.md / AGENTS.md, else a new AGENTIC.md.</summary>
        Private Function AgenticMdPath() As String
            For Each fileName In {"AGENTIC.md", "agentic.md", "AGENTS.md"}
                Dim p = Path.Combine(State.ProjectDir, fileName)
                If IO.File.Exists(p) Then Return p
            Next
            Return Path.Combine(State.ProjectDir, "AGENTIC.md")
        End Function

        ''' <summary>/agentic: shows the project's AGENTIC.md with line numbers; edit, add &lt;rule&gt;, remove &lt;n|text&gt; change it.</summary>
        Private Sub AgenticMd(arg As String)
            Dim file = AgenticMdPath()
            Dim a = If(arg, "").Trim()
            Dim verb = a.Split(" "c)(0).ToLowerInvariant()
            Dim rest = If(a.Length > verb.Length, a.Substring(verb.Length).Trim(), "")
            Dim lines = If(IO.File.Exists(file), IO.File.ReadAllLines(file).ToList(), New List(Of String))
            Select Case verb
                Case "edit", "open"
                    If Not IO.File.Exists(file) Then Paths.WriteText(file, "# Project instructions" & Environment.NewLine & Environment.NewLine)
                    OpenInEditor(file)
                    Out.Success("Saved " & State.Display(file) & " — the next message uses it.")
                Case "add", "+"
                    If rest.Length = 0 Then Out.Fail("Usage: /agentic add <rule>") : Return
                    If lines.Count = 0 Then lines.AddRange({"# Project instructions", ""})
                    lines.Add(If(rest.StartsWith("-") OrElse rest.StartsWith("#") OrElse rest.StartsWith("*"), rest, "- " & rest))
                    Paths.WriteText(file, String.Join(Environment.NewLine, lines) & Environment.NewLine)
                    Out.Success($"Added to {State.Display(file)}: {rest}")
                Case "remove", "rm", "delete", "-"
                    If rest.Length = 0 Then Out.Fail("Usage: /agentic remove <line number | text>") : Return
                    Dim n As Integer
                    Dim index = If(Integer.TryParse(rest, n), n - 1, lines.FindIndex(Function(l) l.IndexOf(rest, StringComparison.OrdinalIgnoreCase) >= 0))
                    If index < 0 OrElse index >= lines.Count Then Out.Fail("No such line in " & State.Display(file)) : Return
                    Dim gone = lines(index)
                    lines.RemoveAt(index)
                    Paths.WriteText(file, String.Join(Environment.NewLine, lines) & Environment.NewLine)
                    Out.Success("Removed: " & gone.Trim())
                Case Else
                    If lines.Count = 0 Then
                        Out.Note($"No {IO.Path.GetFileName(file)} in {State.Display(State.ProjectDir)} yet. /agentic add <rule> or /agentic edit creates it.")
                        Return
                    End If
                    Out.Line(Ansi.Bold(State.Display(file)))
                    For i = 0 To lines.Count - 1
                        Out.Line(Ansi.Muted($"{i + 1,4}  ") & lines(i))
                    Next
                    Out.Line(Ansi.Muted("  /agentic add <rule> · /agentic remove <n|text> · /agentic edit"))
            End Select
        End Sub

        Private Async Function GoalCommandAsync(arg As String) As Task
            arg = If(arg, "").Trim()
            If arg.Length = 0 Then
                Out.Line(If(String.IsNullOrWhiteSpace(_server.Goal), Ansi.Muted("No goal set. Use /goal <what to achieve>; the model keeps working and retries failures until it is met."), "Goal: " & _server.Goal))
                Return
            End If
            If {"clear", "off", "stop", "none", "done"}.Contains(arg.ToLowerInvariant()) Then
                _server.Goal = Nothing
                Out.Success("Goal cleared.")
                Return
            End If
            _server.Goal = If(arg.Length > 4000, arg.Substring(0, 4000), arg)
            Out.Success("Goal set. Working on it; it will keep going and retry failures until done.")
            Await SendAsync("My goal: " & _server.Goal & vbLf & vbLf & "Work on it until it is done.").ConfigureAwait(False)
        End Function

        Private Sub SetMode(arg As String)
            If arg.Length = 0 Then
                Dim names = {"default", "acceptEdits", "plan", "bypassPermissions"}
                Dim hints = {"Ask before edits and commands that change things", "Apply file edits without asking", "Research and plan only; no changes", "Run every tool without asking (careful)"}
                Dim i = Menu.Select(" Permission mode", names.Select(Function(n, k) New MenuOption(n, hints(k))).ToList(), CInt(State.Mode))
                If i < 0 Then Return
                arg = names(i)
            End If
            State.Mode = AgentState.ParseMode(arg, State.Mode)
            Out.Success("Permission mode: " & State.ModeLabel)
        End Sub

        Private Sub Permissions(arg As String)
            Dim parts = CliOptions.SplitList(arg)
            If parts.Count >= 3 AndAlso parts(0).Equals("add", StringComparison.OrdinalIgnoreCase) Then
                Dim kind = parts(1).ToLowerInvariant()
                If kind <> "allow" AndAlso kind <> "deny" AndAlso kind <> "ask" Then
                    Out.Fail("Use /permissions add allow|ask|deny <rule> [--user|--project|--local]")
                    Return
                End If
                Dim scope = If(parts.Contains("--user"), "user", If(parts.Contains("--project"), "project", "local"))
                For Each rule In parts.Skip(2).Where(Function(p) Not p.StartsWith("--"))
                    State.Settings.AddToList(scope, "permissions." & kind, rule)
                    Out.Success($"{kind}: {rule} ({scope} settings)")
                Next
                Return
            End If
            If parts.Count >= 2 AndAlso parts(0).Equals("remove", StringComparison.OrdinalIgnoreCase) Then
                Dim removed = False
                For Each scope In {"local", "project", "user"}
                    For Each kind In {"allow", "deny", "ask"}
                        For Each rule In parts.Skip(1)
                            If State.Settings.RemoveFromList(scope, "permissions." & kind, rule) Then
                                removed = True
                                Out.Success($"Removed {rule} from {kind} ({scope})")
                            End If
                        Next
                    Next
                Next
                SyncLock _perm.SessionAllow
                    _perm.SessionAllow.RemoveAll(Function(r) parts.Skip(1).Contains(r.Raw, StringComparer.OrdinalIgnoreCase))
                End SyncLock
                If Not removed Then Out.Note("No such rule in the settings files.")
                Return
            End If
            Out.Line(Ansi.Bold("Permissions") & Ansi.Muted($"  mode: {State.ModeLabel}"))
            For Each kind In {"allow", "ask", "deny"}
                Dim rules = _perm.Rules(kind)
                Out.Line("  " & Ansi.Accent(kind) & Ansi.Muted($" ({rules.Count})"))
                For Each r In rules
                    Out.Line("    " & r.Raw & Ansi.Muted("  " & r.Source))
                Next
            Next
            Out.Line(Ansi.Muted("  Working folders: " & String.Join("; ", State.WorkingDirs())))
            Out.Note("  /permissions add allow ""Bash(npm test:*)"" [--user|--project|--local] · /permissions remove <rule>")
        End Sub

        Private Async Function ResumePickerAsync() As Task
            Dim local = Sessions.List(State.ProjectDir).Take(20).ToList()
            Dim options = local.Select(Function(s) New MenuOption(If(String.IsNullOrWhiteSpace(s.Title) OrElse s.Title = "New chat", ToolKit.OneLine(s.LastPrompt, 60), s.Title),
                                                                  $"{Ago(s.UpdatedUtc)} · {s.Turns} message{If(s.Turns = 1, "", "s")} · {s.ChatId}", s.ChatId)).ToList()
            options.Add(New MenuOption("Show all conversations on the server…", Nothing, ChrW(1) & "all"))
            Dim i = Menu.Select(" Resume a conversation" & Ansi.Muted("  (this folder)"), options)
            If i < 0 Then Return
            If options(i).Value = ChrW(1) & "all" Then
                Dim all = Await _chat.ListAsync(CancellationToken.None, limit:=40).ConfigureAwait(False)
                Dim j = Menu.Select(" All conversations", all.Select(Function(c) New MenuOption(c.Title, $"{If(c.UpdatedUtc.HasValue, Ago(c.UpdatedUtc.Value), "")} · {ToolKit.OneLine(c.Preview, 70)}", c.Id)).ToList())
                If j < 0 Then Return
                Await OpenConversationAsync(all(j).Id, True).ConfigureAwait(False)
                Return
            End If
            Await OpenConversationAsync(options(i).Value, True).ConfigureAwait(False)
        End Function

        Friend Shared Function Ago(t As DateTime) As String
            Dim d = DateTime.UtcNow - t
            If d.TotalMinutes < 1 Then Return "just now"
            If d.TotalHours < 1 Then Return $"{CInt(d.TotalMinutes)} min ago"
            If d.TotalDays < 1 Then Return $"{CInt(d.TotalHours)} h ago"
            If d.TotalDays < 30 Then Return $"{CInt(d.TotalDays)} d ago"
            Return t.ToLocalTime().ToString("yyyy-MM-dd")
        End Function

        Private Async Function RenameAsync(title As String) As Task
            If String.IsNullOrEmpty(State.ChatId) Then
                Out.Note("Start the conversation first.")
                Return
            End If
            If title.Length = 0 Then title = Menu.Ask("New title:", If(_title, ""))
            If String.IsNullOrWhiteSpace(title) Then Return
            Await _chat.UpdateAsync(State.ChatId, CancellationToken.None, title:=title.Trim()).ConfigureAwait(False)
            _title = title.Trim()
            SetTerminalTitle()
            Out.Success("Renamed to " & _title)
        End Function

        ''' <summary>Go back to an earlier message: restore the files the agent changed since then, cut the conversation there, or both.</summary>
        Private Async Function RewindMenuAsync() As Task
            If State.Checkpoints Is Nothing OrElse State.Checkpoints.Turns().Count = 0 Then
                Out.Note("No earlier messages in this conversation to go back to.")
                Return
            End If
            Dim turns = State.Checkpoints.Turns().OrderByDescending(Function(t) t.Turn).Take(20).ToList()
            Dim options = turns.Select(Function(t) New MenuOption(ToolKit.OneLine(t.Prompt, 70), $"{Ago(t.TimeUtc)}" & If(t.Files.Count > 0, $" · {t.Files.Count} file{If(t.Files.Count = 1, "", "s")} changed", " · no file changes"))).ToList()
            Dim i = Menu.Select(" Rewind to before which message?", options)
            If i < 0 Then Return
            Dim cp = turns(i)
            Dim files = State.Checkpoints.FilesFrom(cp.Turn)
            Dim choices As New List(Of MenuOption)
            If files.Count > 0 Then choices.Add(New MenuOption("Restore code and conversation", $"{files.Count} file(s) go back; the conversation continues from before this message", "both"))
            choices.Add(New MenuOption("Restore conversation only", "Remove this message and everything after it; files stay as they are", "conversation"))
            If files.Count > 0 Then choices.Add(New MenuOption("Restore code only", "Put the files back; keep the conversation", "code"))
            Dim c = Menu.Select(" What should go back?", choices)
            If c < 0 Then Return
            Dim what = choices(c).Value
            If what = "both" OrElse what = "code" Then
                Dim restored = State.Checkpoints.RestoreFrom(cp.Turn)
                For Each f In restored
                    State.ReadFiles.TryRemove(f, Nothing)
                Next
                Out.Success($"Restored {restored.Count} file(s): " & String.Join(", ", restored.Take(8).Select(Function(f) State.Display(f))) & If(restored.Count > 8, " …", ""))
            End If
            If what = "both" OrElse what = "conversation" Then
                If cp.MessageId Is Nothing Then
                    Out.Warning("This message's id is unknown (it was sent before checkpoints existed); the conversation was not changed.")
                Else
                    Try
                        Dim n = Await _chat.RewindAsync(State.ChatId, cp.MessageId, CancellationToken.None).ConfigureAwait(False)
                        State.Checkpoints.DropFrom(cp.Turn)
                        Out.Success($"Conversation rewound ({n} message{If(n = 1, "", "s")} removed).")
                        ' Put the message back in the box to edit and resend.
                        If _editor IsNot Nothing Then
                            SyncLock _queue
                                _typeahead.Clear()
                                _typeahead.Append(cp.Prompt)
                            End SyncLock
                        End If
                    Catch ex As ApiException
                        Out.Fail("The server could not rewind: " & ex.Message)
                    End Try
                End If
            ElseIf what = "code" Then
                State.Checkpoints.DropFrom(cp.Turn)
            End If
        End Function

        Private Async Function ExportAsync(target As String) As Task
            If String.IsNullOrEmpty(State.ChatId) Then
                Out.Note("Nothing to export yet.")
                Return
            End If
            Dim c = Await _chat.GetAsync(State.ChatId, CancellationToken.None).ConfigureAwait(False)
            Dim sb As New StringBuilder()
            sb.AppendLine("# " & c.Title).AppendLine()
            sb.AppendLine($"_Exported from Agentic CLI on {DateTime.Now:yyyy-MM-dd HH:mm} · model {c.Model}_").AppendLine()
            For Each m In c.Messages
                sb.AppendLine(If(m.Role = "user", "## You", "## Agentic")).AppendLine()
                For Each t In m.ToolCalls
                    sb.AppendLine($"> **{Jx.StrOf(t, "name")}** `{ToolKit.OneLine(Jx.StrOf(t, "arguments"), 200)}`")
                Next
                If m.ToolCalls.Count > 0 Then sb.AppendLine()
                sb.AppendLine(m.Content).AppendLine()
            Next
            If target.Equals("clipboard", StringComparison.OrdinalIgnoreCase) Then
                If Clipboard.SetText(sb.ToString()) Then Out.Success("Copied the conversation to the clipboard.") Else Out.Fail("Could not use the clipboard.")
                Return
            End If
            Dim file = If(target.Length > 0, State.Resolve(target), Path.Combine(State.Cwd, $"agentic-{DateTime.Now:yyyyMMdd-HHmm}.md"))
            Paths.WriteText(file, sb.ToString())
            Out.Success("Exported to " & State.Display(file))
        End Function

        Private Sub OpenMemory(which As String)
            Dim files = {("project", Path.Combine(State.ProjectDir, "AGENTS.md")), ("local", Path.Combine(State.ProjectDir, "AGENTS.local.md")), ("user", Paths.UserMemoryFile())}
            Dim pick = Array.FindIndex(files, Function(f) f.Item1.Equals(which, StringComparison.OrdinalIgnoreCase))
            If pick < 0 Then
                Dim loaded = Memory.Load(State.ProjectDir, _includeClaude)
                If loaded.Count > 0 Then
                    Out.Line(Ansi.Bold("Memory files in use"))
                    For Each f In loaded
                        Out.Line("  " & State.Display(f.Path) & Ansi.Muted($"  ({f.Scope}, {f.Content.Length:N0} chars)"))
                    Next
                End If
                pick = Menu.Select(" Open which memory file?", files.Select(Function(f) New MenuOption(f.Item1 & " memory", State.Display(f.Item2))).ToList())
                If pick < 0 Then Return
            End If
            Dim file = files(pick).Item2
            If Not IO.File.Exists(file) Then Paths.WriteText(file, "# " & If(files(pick).Item1 = "user", "My instructions for every project", "Project instructions") & Environment.NewLine & Environment.NewLine)
            OpenInEditor(file)
            Out.Success("Saved " & State.Display(file))
        End Sub

        ''' <summary>Opens a file in your editor and waits until it is closed; False when no editor could start.</summary>
        Friend Shared Function OpenInEditor(file As String) As Boolean
            Dim editor = If(Environment.GetEnvironmentVariable("VISUAL"), Environment.GetEnvironmentVariable("EDITOR"))
            Try
                Dim psi As ProcessStartInfo
                If String.IsNullOrWhiteSpace(editor) AndAlso OperatingSystem.IsWindows() Then
                    psi = New ProcessStartInfo("notepad.exe")
                    psi.ArgumentList.Add(file)
                ElseIf String.IsNullOrWhiteSpace(editor) Then
                    Dim found = {"nano", "vim", "vi"}.Select(Function(e) Platform.Which(e)).FirstOrDefault(Function(e) e IsNot Nothing)
                    If found Is Nothing Then Throw New InvalidOperationException("no editor found; set EDITOR (for example EDITOR=nano)")
                    psi = New ProcessStartInfo(found)
                    psi.ArgumentList.Add(file)
                Else
                    psi = Platform.ShellStart(editor & " """ & file & """")
                End If
                psi.UseShellExecute = False
                Out.Note("Opening " & file & " — close the editor to continue.")
                Using p = Process.Start(psi)
                    p.WaitForExit()
                End Using
                Return True
            Catch ex As Exception When TypeOf ex Is ComponentModel.Win32Exception OrElse TypeOf ex Is InvalidOperationException
                Out.Fail("Could not open an editor: " & ex.Message)
                Return False
            End Try
        End Function

        Private Sub AddDir(p As String)
            If p.Length = 0 Then
                Out.Line(Ansi.Muted("Working folders: " & String.Join("; ", State.WorkingDirs())))
                Return
            End If
            Dim full = State.Resolve(p)
            If Not Directory.Exists(full) Then
                Out.Fail("No such folder: " & full)
                Return
            End If
            State.ExtraDirs.Add(full)
            State.Mcp.Roots = State.WorkingDirs()
            Out.Success("Added " & full & " for this session" & Ansi.Muted(" (to keep it: /config add permissions.additionalDirectories <path>)"))
        End Sub

        Private Sub Agents(arg As String)
            If arg.Equals("new", StringComparison.OrdinalIgnoreCase) OrElse arg.Equals("create", StringComparison.OrdinalIgnoreCase) Then
                Dim name = Menu.Ask("Name (e.g. test-runner):")
                If String.IsNullOrWhiteSpace(name) Then Return
                Dim desc = Menu.Ask("When should it be used? (description):")
                Dim tools = Menu.Ask("Tools (comma-separated; empty for all):")
                Dim prompt = Menu.Ask("Its instructions (system prompt):")
                Dim scope = If(Menu.Select(" Save for", {New MenuOption("This project (.agentic\agents)"), New MenuOption("All my projects (~\.agentic\agents)")}) = 1, "user", "project")
                Dim file = Subagents.Create(State.ProjectDir, scope, name, If(desc, ""), tools, If(prompt, ""))
                Out.Success("Created " & State.Display(file) & Ansi.Muted(" (edit it to refine the instructions)"))
                Return
            End If
            Out.Line(Ansi.Bold("Subagents") & Ansi.Muted("  (the model starts them with the Task tool; /agents new to create one)"))
            For Each a In Subagents.All(State.ProjectDir, _includeClaude)
                Out.Line("  " & Ansi.Accent(Ansi.Pad(a.Name, 22)) & " " & ToolKit.OneLine(a.Description, Math.Max(30, Ansi.TermWidth() - 40)) & Ansi.Muted("  " & a.Source))
            Next
        End Sub

        Private Sub SkillsCommand(arg As String)
            If arg.Equals("new", StringComparison.OrdinalIgnoreCase) Then
                Dim kindPick = Menu.Select(" Create a", {New MenuOption("Command", "a prompt you run as /name (with $ARGUMENTS)"), New MenuOption("Skill", "instructions the model loads when a request matches")})
                If kindPick < 0 Then Return
                Dim name = Menu.Ask("Name:")
                If String.IsNullOrWhiteSpace(name) Then Return
                Dim desc = Menu.Ask("Description:")
                Dim body = Menu.Ask(If(kindPick = 0, "Prompt ($ARGUMENTS for what follows the command):", "Instructions:"))
                Dim scope = If(Menu.Select(" Save for", {New MenuOption("This project"), New MenuOption("All my projects")}) = 1, "user", "project")
                Dim file = State.Skills.Create(If(kindPick = 0, "command", "skill"), name, If(desc, ""), If(body, ""), scope)
                Out.Success("Created " & State.Display(file))
                Return
            End If
            If arg.Length > 0 Then
                Dim s = State.Skills.FindByName(arg)
                If s Is Nothing Then
                    Out.Fail("No skill or command " & arg)
                Else
                    Out.Line(Ansi.Bold("/" & s.Name) & Ansi.Muted($"  {s.Kind} · {s.Source} · {s.Location}"))
                    Out.Write(MarkdownStream.Render(s.Body, "  ", "  "))
                End If
                Return
            End If
            Dim all = State.Skills.All()
            If all.Count = 0 Then
                Out.Note("No skills or commands yet. Put commands in .agentic\commands\*.md or skills in .agentic\skills\<name>\SKILL.md (Claude Code's ~\.claude folders work too), or use /skills new.")
                Return
            End If
            For Each kind In {"skill", "command"}
                Dim items = all.Where(Function(s) s.Kind = kind).ToList()
                If items.Count = 0 Then Continue For
                Out.Line(Ansi.Bold(If(kind = "skill", "Skills", "Commands")))
                For Each s In items
                    Out.Line("  " & Ansi.Accent(Ansi.Pad("/" & s.Name, 28)) & " " & ToolKit.OneLine(s.Description, Math.Max(30, Ansi.TermWidth() - 50)) & Ansi.Muted("  " & s.Source))
                Next
            Next
        End Sub

        Private Async Function McpCommandAsync(arg As String) As Task
            Dim parts = arg.Split(" "c, StringSplitOptions.RemoveEmptyEntries)
            Dim verb = If(parts.Length > 0, parts(0).ToLowerInvariant(), "")
            Dim id = If(parts.Length > 1, parts(1), Nothing)
            Select Case verb
                Case "catalog", "browse"
                    Dim installed = State.Mcp.Configured().Select(Function(x) x.Id).ToHashSet(StringComparer.OrdinalIgnoreCase)
                    For Each l In Mcp.McpCatalog.Describe(installed)
                        If l.StartsWith("  ") Then Out.Line(Ansi.Muted(l)) Else Out.Line(Ansi.Bold(l))
                    Next
                    Out.Note("Add one: /mcp add <name> [folder]   (API keys are read from your environment; see agentic mcp add -e)")
                    Return
                Case "add"
                    ' A well-known server by name, connected right away: /mcp add playwright, /mcp add filesystem C:work.
                    Dim entry = Mcp.McpCatalog.Find(id)
                    If entry Is Nothing Then Out.Fail("Not in the catalog: " & If(id, "(none)") & ". See /mcp catalog, or use agentic mcp add <name> -- <command>.") : Return
                    Dim folder = IO.Path.GetFullPath(If(parts.Length > 2, String.Join(" ", parts.Skip(2)), Environment.CurrentDirectory))
                    Dim s = Mcp.McpCatalog.ToServer(entry, folder, Nothing)
                    Mcp.McpConfig.Upsert(s, "user", State.ProjectDir)
                    Out.StartSpinner("Connecting " & entry.Name & "…")
                    Await State.Mcp.ConnectOneAsync(s).ConfigureAwait(False)
                    Out.StopSpinner()
                    Dim c = State.Mcp.Get(s.Id)
                    If c IsNot Nothing AndAlso c.Status.Connected Then Out.Success($"{entry.Name}: {c.Status.Tools.Count} tools") Else Out.Fail($"{entry.Name} added but not connected: " & If(c?.Status.Error, "needs " & entry.Needs))
                    For Each k In entry.Env.Where(Function(x) String.IsNullOrEmpty(Environment.GetEnvironmentVariable(x.Key)))
                        Out.Note($"Set {k.Key}: {k.Hint}, then /mcp reconnect {s.Id}")
                    Next
                    Return
                Case "reconnect"
                    If id Is Nothing Then Exit Select
                    Out.StartSpinner("Connecting " & id & "…")
                    Await State.Mcp.ReconnectAsync(id).ConfigureAwait(False)
                    Out.StopSpinner()
                    Dim c = State.Mcp.Get(id)
                    If c IsNot Nothing AndAlso c.Status.Connected Then Out.Success($"{id}: connected, {c.Status.Tools.Count} tools") Else Out.Fail($"{id}: {c?.Status.Error}")
                    Return
                Case "enable", "disable"
                    If id Is Nothing Then Exit Select
                    Dim s = State.Mcp.Configured().FirstOrDefault(Function(x) x.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
                    If s Is Nothing Then
                        Out.Fail("No MCP server " & id)
                        Return
                    End If
                    If s.Scope = "project" Then
                        State.Settings.RemoveFromList("local", If(verb = "enable", "disabledMcpjsonServers", "enabledMcpjsonServers"), s.Id)
                        State.Settings.AddToList("local", If(verb = "enable", "enabledMcpjsonServers", "disabledMcpjsonServers"), s.Id)
                    ElseIf Not s.Editable Then
                        ' Claude Code's, a plugin's or Claude Desktop's server: remembered here, their files stay as they are.
                        If verb = "enable" Then State.Settings.RemoveFromList("user", "disabledMcpServers", s.Id) Else State.Settings.AddToList("user", "disabledMcpServers", s.Id)
                    Else
                        Mcp.McpConfig.SetEnabled(s.Id, verb = "enable", s.Scope, State.ProjectDir)
                    End If
                    s.Enabled = verb = "enable"
                    If verb = "enable" Then Await State.Mcp.ConnectOneAsync(s).ConfigureAwait(False) Else State.Mcp.Disconnect(s.Id)
                    Out.Success($"{s.Id} {verb}d")
                    Return
                Case "login", "logout"
                    ' Hosted servers that use OAuth: sign in through the browser (or forget the sign-in).
                    Dim s = State.Mcp.Configured().FirstOrDefault(Function(x) x.Id.Equals(If(id, ""), StringComparison.OrdinalIgnoreCase))
                    If s Is Nothing OrElse s.Transport = Mcp.McpTransportKind.Stdio Then
                        Out.Fail("Give an HTTP MCP server: /mcp " & verb & " <id>")
                        Return
                    End If
                    Dim url = Mcp.McpConfig.Expand(s.Endpoint, State.ProjectDir, s.PluginRoot)
                    If verb = "logout" Then
                        Mcp.McpOAuth.SignOut(url)
                        State.Mcp.Disconnect(s.Id)
                        Out.Success("Signed out of " & s.Name)
                        Return
                    End If
                    Out.Note("Opening the browser to sign in to " & s.Name & "… (waiting up to 5 minutes)")
                    Try
                        Await Mcp.McpOAuth.SignInAsync(url, State.Mcp.Get(s.Id)?.WwwAuthenticate, s.OAuthClientId, s.OAuthClientSecret, s.OAuthScope, s.OAuthCallbackPort).ConfigureAwait(False)
                    Catch ex As Exception When TypeOf ex Is InvalidOperationException OrElse TypeOf ex Is TimeoutException OrElse TypeOf ex Is Net.Http.HttpRequestException OrElse TypeOf ex Is Net.Sockets.SocketException
                        Out.Fail("Sign-in failed: " & ex.Message)
                        Return
                    End Try
                    Out.StartSpinner("Connecting " & s.Name & "…")
                    Await State.Mcp.ConnectOneAsync(s).ConfigureAwait(False)
                    Out.StopSpinner()
                    Dim conn = State.Mcp.Get(s.Id)
                    If conn IsNot Nothing AndAlso conn.Status.Connected Then Out.Success($"Signed in: {s.Name}, {conn.Status.Tools.Count} tools") Else Out.Fail($"Signed in, but {s.Name} did not connect: {conn?.Status.Error}")
                    Return
                Case "tools"
                    Dim c = If(id Is Nothing, Nothing, State.Mcp.Get(id))
                    If c Is Nothing Then
                        Out.Fail("Give a connected server: /mcp tools <id>")
                        Return
                    End If
                    For Each t In c.Status.Tools
                        Out.Line("  " & Ansi.Accent(McpTool.ToolName(c.Options.Id, t.Name)) & "  " & Ansi.Muted(ToolKit.OneLine(t.Description, 100)))
                    Next
                    Return
                Case "server"
                    Dim s = _meta.Servers.FirstOrDefault(Function(x) x.Id.Equals(If(id, ""), StringComparison.OrdinalIgnoreCase))
                    If s Is Nothing Then
                        Out.Fail("No server-side MCP server " & id)
                        Return
                    End If
                    If _server.McpServers.Contains(s.Id) Then _server.McpServers.Remove(s.Id) Else _server.McpServers.Add(s.Id)
                    State.Settings.SetValue("local", "serverMcpServers", New JsonArray(_server.McpServers.Select(Function(x) CType(JsonValue.Create(x), JsonNode)).ToArray()))
                    Out.Success($"{s.Name} on the server: {If(_server.McpServers.Contains(s.Id), "on", "off")}")
                    Return
            End Select
            Out.Line(Ansi.Bold("MCP servers on this computer") & Ansi.Muted("  (/mcp catalog for well-known ones; /mcp reconnect|enable|disable|tools|login|logout <id>)"))
            Dim configured = State.Mcp.Configured()
            If configured.Count = 0 Then Out.Note("  none — add one with: agentic mcp add <name> -- <command> [args]   or   agentic mcp import")
            For Each s In configured
                Dim c = State.Mcp.Get(s.Id)
                Dim status = If(Not s.Enabled, Ansi.Muted("disabled"), If(c Is Nothing, Ansi.Muted("not started"), If(c.Status.Connected, Ansi.Ok($"connected · {c.Status.Tools.Count} tools"),
                                If(c.Status.Connecting, Ansi.Warn("connecting…"), If(c.Status.NeedsSignIn, Ansi.Warn("sign-in needed: /mcp login " & s.Id), Ansi.Bad("failed: " & ToolKit.OneLine(c.Status.Error, 80)))))))
                Out.Line("  " & Ansi.Accent(Ansi.Pad(s.Id, 20)) & " " & status & Ansi.Muted($"  {s.TransportName()} · {If(s.Source, s.Scope)}"))
            Next
            If _meta.Servers.Count > 0 Then
                Out.Line(Ansi.Bold("MCP servers on the Agentic server") & Ansi.Muted("  (/mcp server <id> toggles)"))
                For Each s In _meta.Servers
                    Out.Line("  " & Ansi.Accent(Ansi.Pad(s.Id, 20)) & " " & If(_server.McpServers.Contains(s.Id), Ansi.Ok("on"), Ansi.Muted("off")) &
                             Ansi.Muted($"  {s.Name} · {s.Tools} tools" & If(s.Connected, "", " · not connected")))
                Next
            End If
        End Function

        Private Sub ShowHooks()
            Dim events = {"PreToolUse", "PermissionRequest", "PostToolUse", "PostToolUseFailure", "UserPromptSubmit", "Stop", "StopFailure", "SubagentStart", "SubagentStop",
                          "SessionStart", "SessionEnd", "Notification", "PreCompact", "PostCompact", "CwdChanged"}
            Dim any = False
            For Each ev In events
                Dim hooks = State.Settings.Hooks(ev)
                If hooks.Count = 0 Then Continue For
                any = True
                Out.Line(Ansi.Bold(ev))
                For Each h In hooks
                    Out.Line("  " & If(String.IsNullOrEmpty(h.Matcher), "*", h.Matcher) & Ansi.Muted(" → ") & h.Command & Ansi.Muted($"  ({h.Scope}, {h.TimeoutSeconds}s)"))
                Next
            Next
            If Not any Then Out.Note("No hooks. Add them to settings.json in Claude Code's format, e.g. {""hooks"":{""PostToolUse"":[{""matcher"":""Edit|Write"",""hooks"":[{""type"":""command"",""command"":""dotnet format""}]}]}}")
        End Sub

        Private Sub ConfigCommand(arg As String)
            Dim parts = CliOptions.SplitList(arg)
            If parts.Count >= 3 AndAlso (parts(0) = "set" OrElse parts(0) = "add") Then
                Dim scope = If(parts.Contains("--project"), "project", If(parts.Contains("--local"), "local", "user"))
                Dim values = parts.Skip(2).Where(Function(p) Not p.StartsWith("--")).ToList()
                If parts(0) = "add" Then
                    For Each v In values
                        State.Settings.AddToList(scope, parts(1), v)
                    Next
                Else
                    State.Settings.SetValue(scope, parts(1), ConfigValue(String.Join(" ", values)))
                End If
                Out.Success($"{parts(1)} updated in the {scope} settings ({State.Display(State.Settings.Scope(scope).File)})")
                ApplyLiveSetting(parts(1))
                Return
            End If
            Out.Line(Ansi.Bold("Settings") & Ansi.Muted("  (later files win; /config set <key> <value> [--user|--project|--local])"))
            For Each s In State.Settings.Scopes
                Dim keys = s.Root.Select(Function(kv) kv.Key).ToList()
                If s.File Is Nothing AndAlso keys.Count = 0 Then Continue For
                Out.Line("  " & Ansi.Accent(Ansi.Pad(s.Name, 9)) & " " & If(s.File Is Nothing, Ansi.Muted("(in memory)"), State.Display(s.File) & If(IO.File.Exists(s.File), "", Ansi.Muted(" (not created)"))))
                For Each kv In s.Root
                    Out.Line("      " & kv.Key & Ansi.Muted(" = " & ToolKit.OneLine(If(kv.Value?.ToJsonString(), "null"), 100)))
                Next
            Next
            Out.Line(Ansi.Muted("  Keys: model, effort, permissions.{allow,ask,deny,defaultMode,additionalDirectories}, hooks, env, shell, outputStyle, maxTurns,"))
            Out.Line(Ansi.Muted("        webSearch, codeInterpreter, serverMcpServers, showThinking, verbose, notifications, statusLine, includeClaudeConfig"))
        End Sub

        Friend Shared Function ConfigValue(text As String) As JsonNode
            Dim t = text.Trim()
            Dim b As Boolean
            If Boolean.TryParse(t, b) Then Return JsonValue.Create(b)
            Dim n As Integer
            If Integer.TryParse(t, n) Then Return JsonValue.Create(n)
            If t.StartsWith("{") OrElse t.StartsWith("[") Then
                Try
                    Return JsonNode.Parse(t)
                Catch ex As Text.Json.JsonException
                End Try
            End If
            Return JsonValue.Create(t.Trim(""""c))
        End Function

        Private Sub ApplyLiveSetting(key As String)
            Select Case key
                Case "maxTurns" : _maxTurns = State.Settings.Int("maxTurns", _maxTurns)
                Case "outputStyle" : _outputStyle = State.Settings.Str("outputStyle", _outputStyle)
                Case "effort" : _server.ReasoningEffort = NormalizeEffort(State.Settings.Str("effort", "medium"))
                Case "showThinking" : State.ShowThinking = State.Settings.Bool("showThinking", False)
                Case "verbose" : State.Verbose = State.Settings.Bool("verbose", False)
                Case "model" : _model = PickModel(State.Settings.Str("model"))
            End Select
        End Sub

        Private Sub Toggle(key As String, arg As String, available As Boolean, apply As Action(Of Boolean), current As Func(Of Boolean), label As String)
            If Not available Then
                Out.Fail(label & " is not available on this server or for your account.")
                Return
            End If
            Dim v = If(arg.Length = 0, Not current(), arg.Equals("on", StringComparison.OrdinalIgnoreCase) OrElse arg.Equals("true", StringComparison.OrdinalIgnoreCase))
            apply(v)
            State.Settings.SetValue("local", key, JsonValue.Create(v))
            Out.Success(label & If(v, " on", " off"))
        End Sub

        Private Sub SetStyle(arg As String)
            If arg.Length = 0 Then
                Dim hints = {"Concise, task-focused answers", "Explains choices and patterns while working", "Teaches: asks you to write small key pieces", "Even briefer"}
                Dim i = Menu.Select(" Output style", Instructions.Styles.Select(Function(s, k) New MenuOption(s, hints(k))).ToList(), Math.Max(0, Array.IndexOf(Instructions.Styles, _outputStyle)))
                If i < 0 Then Return
                arg = Instructions.Styles(i)
            End If
            If Not Instructions.Styles.Contains(arg.ToLowerInvariant()) Then
                Out.Fail("Styles: " & String.Join(", ", Instructions.Styles))
                Return
            End If
            _outputStyle = arg.ToLowerInvariant()
            State.Settings.SetValue("local", "outputStyle", JsonValue.Create(_outputStyle))
            Out.Success("Output style: " & _outputStyle)
        End Sub

        Private Sub ShowStatus()
            Dim m = _meta.Find(_model)
            Out.Line(Ansi.Bold(AppInfo.Name) & Ansi.Muted(" v" & AppInfo.Version))
            Dim rows As New List(Of (String, String)) From {
                ("Server", _creds.ServerUrl & Ansi.Muted("  (key " & _creds.MaskedKey() & ")")),
                ("Account", If(_meta.IsAdmin, "administrator", "user") & If(_meta.CanTools, "", Ansi.Warn(" · no server tools"))),
                ("Model", If(m?.Name, _model) & If(m IsNot Nothing AndAlso m.Custom, Ansi.Muted(" (custom: " & String.Join(", ", m.Routes) & ")"), "")),
                ("Effort", _server.ReasoningEffort),
                ("Mode", State.ModeLabel),
                ("Conversation", If(State.ChatId Is Nothing, Ansi.Muted("(new)"), If(_title, "") & Ansi.Muted("  " & State.ChatId))),
                ("Working dir", State.Cwd),
                ("Folders", String.Join("; ", State.WorkingDirs())),
                ("Git", If(State.Project.IsGit, If(State.Project.Branch(), "?") & Ansi.Muted("  " & State.Project.GitRoot), Ansi.Muted("not a repository"))),
                ("Shell", State.Shells.Describe()),
                ("Web search", If(_server.WebSearch, "on", "off") & " · code interpreter " & If(_server.CodeInterpreter, "on", "off")),
                ("Memory", OrNone(String.Join(", ", Memory.Load(State.ProjectDir, _includeClaude).Select(Function(f) State.Display(f.Path))), "none")),
                ("MCP", $"{State.Mcp.ConnectedServers().Count} of {State.Mcp.Configured().Count} connected" & If(_server.McpServers.Count > 0, $" · {_server.McpServers.Count} on the server", "")),
                ("Skills", $"{State.Skills.Skills().Count} skills, {State.Skills.Commands().Count} commands"),
                ("Output style", _outputStyle),
                ("Settings", OrNone(String.Join(", ", State.Settings.Scopes.Where(Function(s) s.File IsNot Nothing AndAlso IO.File.Exists(s.File)).Select(Function(s) s.Name)), "defaults"))}
            For Each r In rows
                Out.Line("  " & Ansi.Muted(Ansi.Pad(r.Item1, 14)) & r.Item2)
            Next
        End Sub

        Private Shared Function OrNone(s As String, fallback As String) As String
            Return If(String.IsNullOrWhiteSpace(s), fallback, s)
        End Function

        Private Sub Bashes(arg As String)
            If arg.StartsWith("kill ", StringComparison.OrdinalIgnoreCase) Then
                Dim id = arg.Substring(5).Trim()
                If State.Shells.Kill(id) Then Out.Success("Stopped " & id) Else Out.Fail("No running shell " & id)
                Return
            End If
            Dim all = State.Shells.All()
            If all.Count = 0 Then
                Out.Note("No background commands.")
                Return
            End If
            For Each s In all
                Out.Line("  " & Ansi.Accent(s.Id) & " " & If(s.Running, Ansi.Ok("running"), Ansi.Muted(If(s.Killed, "killed", "exit " & s.ExitCode))) & "  " &
                         ToolKit.OneLine(s.CommandText, 70) & Ansi.Muted("  " & Ago(s.StartedUtc)))
            Next
            Out.Note("  /bashes kill <id> to stop one")
        End Sub

        Private Const InitPrompt As String = "Please analyze this codebase and create an AGENTS.md file in the project root, which will be given to future instances of Agentic (and other coding agents) working in this repository.

What to add:
1. Commands that will be commonly used, such as how to build, lint, and run tests. Include the necessary commands to develop in this codebase, such as how to run a single test.
2. High-level code architecture and structure so that future instances can be productive more quickly. Focus on the ""big picture"" architecture that requires reading multiple files to understand.

Usage notes:
- If there's already an AGENTS.md (or a CLAUDE.md), suggest improvements to it instead of replacing it.
- When you make the initial AGENTS.md, do not repeat yourself and do not include obvious instructions like ""Provide helpful error messages to users"", ""Write unit tests for all new utilities"", ""Never include sensitive information (API keys, tokens) in code or commits"".
- Avoid listing every component or file structure that can be easily discovered.
- Don't include generic development practices.
- If there are Cursor rules (.cursorrules or .cursor/rules), Copilot rules (.github/copilot-instructions.md) or Cline rules (.clinerules), make sure to include the important parts.
- If there is a README.md, make sure to include the important parts.
- Do not make up information such as ""Common Development Tasks"", ""Tips for Development"", ""Support and Documentation"" unless this is expressly included in other files that you read.
- Be sure to prefix the file with the following text:

```
# AGENTS.md

This file provides guidance to coding agents (Agentic CLI, Claude Code and others) when working with code in this repository.
```"

        Private Shared Function ReviewPrompt(arg As String) As String
            If arg.Trim().Length > 0 Then
                Return $"Review pull request {arg.Trim()}. Use `gh pr view {arg.Trim()}` and `gh pr diff {arg.Trim()}` to get its details and changes (ask me if gh is not available). " &
                       "Give a concise review: an overview of what the PR does, then concrete issues (correctness, edge cases, security, performance, style consistency) with file:line references, ordered by importance, and suggestions. Do not change any files."
            End If
            Return "Review the pending changes in this repository (staged and unstaged: run git status and git diff, plus git diff --cached). " &
                   "Give a concise code review: what the changes do, then concrete problems (bugs, edge cases, security, performance, missing tests, inconsistencies with the surrounding code) with file:line references, ordered by importance, and suggested fixes. Do not change any files."
        End Function

        Private Const SecurityReviewPrompt As String = "Do a security review of the pending changes on this branch (git diff against the main branch, plus uncommitted changes). " &
            "Focus on high-confidence, exploitable vulnerabilities introduced by these changes: injection (SQL, command, path traversal), authentication and authorization flaws, unsafe deserialization, secrets in code, " &
            "XSS/CSRF, SSRF, insecure crypto and data exposure. For each finding give the file:line, the vulnerability, an exploit scenario, its severity and a fix. Skip theoretical or style issues. Do not change any files."

    End Class

    ''' <summary>Text to the clipboard: the Windows clipboard; on Linux wl-copy, xclip or xsel, else the terminal (OSC 52).</summary>
    Friend NotInheritable Class Clipboard

        <DllImport("user32.dll", SetLastError:=True)>
        Private Shared Function OpenClipboard(hWndNewOwner As IntPtr) As Boolean
        End Function
        <DllImport("user32.dll", SetLastError:=True)>
        Private Shared Function CloseClipboard() As Boolean
        End Function
        <DllImport("user32.dll", SetLastError:=True)>
        Private Shared Function EmptyClipboard() As Boolean
        End Function
        <DllImport("user32.dll", SetLastError:=True)>
        Private Shared Function SetClipboardData(uFormat As UInteger, hMem As IntPtr) As IntPtr
        End Function
        <DllImport("kernel32.dll", SetLastError:=True)>
        Private Shared Function GlobalAlloc(uFlags As UInteger, dwBytes As UIntPtr) As IntPtr
        End Function
        <DllImport("kernel32.dll", SetLastError:=True)>
        Private Shared Function GlobalLock(hMem As IntPtr) As IntPtr
        End Function
        <DllImport("kernel32.dll", SetLastError:=True)>
        Private Shared Function GlobalUnlock(hMem As IntPtr) As Boolean
        End Function

        Public Shared Function SetText(text As String) As Boolean
            If Not OperatingSystem.IsWindows() Then Return SetTextUnix(text)
            Dim opened = False
            For attempt = 1 To 10
                If OpenClipboard(IntPtr.Zero) Then
                    opened = True
                    Exit For
                End If
                Thread.Sleep(30)
            Next
            If Not opened Then Return False
            Try
                EmptyClipboard()
                Dim bytes = (text & ChrW(0)).Length * 2
                Dim h = GlobalAlloc(&H2UI, New UIntPtr(CUInt(bytes)))
                If h = IntPtr.Zero Then Return False
                Dim p = GlobalLock(h)
                Marshal.Copy((text & ChrW(0)).ToCharArray(), 0, p, text.Length + 1)
                GlobalUnlock(h)
                Return SetClipboardData(13UI, h) <> IntPtr.Zero
            Finally
                CloseClipboard()
            End Try
        End Function

        Private Shared Function SetTextUnix(text As String) As Boolean
            Dim tools As New List(Of (Exe As String, Args As String()))
            If Not String.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")) Then tools.Add(("wl-copy", Array.Empty(Of String)()))
            If Not String.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")) Then
                tools.Add(("xclip", {"-selection", "clipboard"}))
                tools.Add(("xsel", {"--clipboard", "--input"}))
            End If
            For Each t In tools
                Dim exe = Platform.Which(t.Exe)
                If exe Is Nothing Then Continue For
                Try
                    ' Not redirecting their output: xclip keeps serving the selection from a child process.
                    Dim psi As New ProcessStartInfo(exe) With {.UseShellExecute = False, .RedirectStandardInput = True}
                    For Each a In t.Args
                        psi.ArgumentList.Add(a)
                    Next
                    Using p = Process.Start(psi)
                        p.StandardInput.Write(text)
                        p.StandardInput.Close()
                        If p.WaitForExit(3000) AndAlso p.ExitCode = 0 Then Return True
                    End Using
                Catch ex As Exception When TypeOf ex Is ComponentModel.Win32Exception OrElse TypeOf ex Is IOException OrElse TypeOf ex Is InvalidOperationException
                End Try
            Next
            ' No clipboard program (a server, SSH): most terminals take OSC 52 and put the text on the local clipboard.
            If Console.IsOutputRedirected Then Return False
            Console.Out.Write(ChrW(27) & "]52;c;" & Convert.ToBase64String(Encoding.UTF8.GetBytes(text)) & ChrW(7))
            Console.Out.Flush()
            Return True
        End Function

    End Class

End Namespace
