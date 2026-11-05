Option Strict On
Option Explicit On
Option Infer On

Namespace Core

    ''' <summary>Command-line options (the same names as Claude Code's where they exist).</summary>
    Friend NotInheritable Class CliOptions

        Public Property Print As Boolean
        Public Property Prompt As String
        Public Property OutputFormat As String = "text"
        ''' <summary>stream-json input: who answers permission questions: host (the driving program) or deny.</summary>
        Public Property PermissionPrompt As String
        Public Property InputFormat As String = "text"
        Public Property ContinueLast As Boolean
        Public Property ResumePick As Boolean
        Public Property ResumeId As String
        Public Property Model As String
        Public Property PermissionMode As String
        Public Property SkipPermissions As Boolean
        Public Property AllowedTools As New List(Of String)
        Public Property DisallowedTools As New List(Of String)
        Public Property AppendSystemPrompt As String
        Public Property SystemPrompt As String
        Public Property AddDirs As New List(Of String)
        Public Property McpConfigs As New List(Of String)
        Public Property SettingsFile As String
        Public Property MaxTurns As Integer
        Public Property Verbose As Boolean
        Public Property Debug As Boolean
        Public Property Effort As String
        Public Property Web As Boolean?
        Public Property Shell As String
        Public Property NoColor As Boolean
        Public Property Help As Boolean
        Public Property Version As Boolean
        Public Property SelfTest As Boolean
        ''' <summary>-n/--name: the title of the new conversation.</summary>
        Public Property Name As String
        ''' <summary>--tools: only these built-in tools exist (Nothing = all; empty = none).</summary>
        Public Property Tools As List(Of String)
        ''' <summary>--strict-mcp-config: only the MCP servers given with --mcp-config.</summary>
        Public Property StrictMcpConfig As Boolean
        ''' <summary>--fork-session: with --continue/--resume, continue in a copy and leave the original as it was.</summary>
        Public Property ForkSession As Boolean
        ''' <summary>--no-session-persistence: a temporary conversation that is not kept in the history.</summary>
        Public Property NoSessionPersistence As Boolean
        ''' <summary>--replay-user-messages: stream-json echoes each user message back on stdout.</summary>
        Public Property ReplayUserMessages As Boolean
        ''' <summary>--disable-slash-commands: no custom commands or skills.</summary>
        Public Property DisableSlashCommands As Boolean
        ''' <summary>--fallback-model: answers when the model is overloaded or unavailable.</summary>
        Public Property FallbackModel As String
        ''' <summary>--max-budget-usd: stop once the session's model calls cost this much (print mode).</summary>
        Public Property MaxBudgetUsd As Decimal?
        ''' <summary>-w/--worktree [name]: work in a git worktree of that name ("" = a new name); Nothing = not asked.</summary>
        Public Property Worktree As String
        ''' <summary>-i/--image: images attached to the first message.</summary>
        Public Property Images As New List(Of String)
        Public Property Command As String
        Public Property CommandArgs As New List(Of String)
        Public Property Errors As New List(Of String)

        Private Shared ReadOnly Subcommands As String() = {"mcp", "config", "doctor", "login", "setup", "logout", "auth", "sessions", "update", "upgrade", "help", "version", "agents", "skills"}

        Public Shared Function Parse(args As String()) As CliOptions
            Dim o As New CliOptions()
            Dim positional As New List(Of String)
            Dim i = 0
            Dim nextValue = Function() As String
                                If i + 1 < args.Length Then
                                    i += 1
                                    Return args(i)
                                End If
                                o.Errors.Add($"{args(i)} needs a value")
                                Return Nothing
                            End Function
            ' Takes the following values until the next option (for lists like --allowedTools "Bash(git:*)" Edit).
            Dim values = Function() As List(Of String)
                             Dim list As New List(Of String)
                             While i + 1 < args.Length AndAlso Not args(i + 1).StartsWith("-")
                                 i += 1
                                 list.AddRange(SplitList(args(i)))
                             End While
                             Return list
                         End Function
            While i < args.Length
                Dim a = args(i)
                Dim eq = If(a.StartsWith("--"), a.IndexOf("="c), -1)
                Dim key = If(eq > 0, a.Substring(0, eq), a)
                Dim inline = If(eq > 0, a.Substring(eq + 1), Nothing)
                Dim valueOf = Function() If(inline, nextValue())
                Select Case key
                    Case "-p", "--print"
                        o.Print = True
                    Case "--output-format" : o.OutputFormat = If(valueOf(), "text").ToLowerInvariant()
                    Case "--input-format" : o.InputFormat = If(valueOf(), "text").ToLowerInvariant()
                    Case "--permission-prompt", "--permission-prompt-tool" : o.PermissionPrompt = If(valueOf(), "host").ToLowerInvariant()
                    Case "-c", "--continue" : o.ContinueLast = True
                    Case "-r", "--resume"
                        o.ResumePick = True
                        If inline IsNot Nothing Then
                            o.ResumeId = inline
                        ElseIf i + 1 < args.Length AndAlso Not args(i + 1).StartsWith("-") AndAlso LooksLikeId(args(i + 1)) Then
                            i += 1
                            o.ResumeId = args(i)
                        End If
                    Case "-m", "--model" : o.Model = valueOf()
                    Case "--permission-mode" : o.PermissionMode = valueOf()
                    Case "--dangerously-skip-permissions" : o.SkipPermissions = True
                    Case "--allowedTools", "--allowed-tools"
                        If inline IsNot Nothing Then o.AllowedTools.AddRange(SplitList(inline)) Else o.AllowedTools.AddRange(values())
                    Case "--disallowedTools", "--disallowed-tools"
                        If inline IsNot Nothing Then o.DisallowedTools.AddRange(SplitList(inline)) Else o.DisallowedTools.AddRange(values())
                    Case "--append-system-prompt" : o.AppendSystemPrompt = JoinPrompt(o.AppendSystemPrompt, valueOf())
                    Case "--system-prompt" : o.SystemPrompt = valueOf()
                    Case "--system-prompt-file" : o.SystemPrompt = ReadPromptFile(valueOf(), o)
                    Case "--append-system-prompt-file" : o.AppendSystemPrompt = JoinPrompt(o.AppendSystemPrompt, ReadPromptFile(valueOf(), o))
                    Case "-n", "--name" : o.Name = valueOf()
                    Case "--tools"
                        o.Tools = If(o.Tools, New List(Of String))
                        If inline IsNot Nothing Then o.Tools.AddRange(SplitList(inline)) Else o.Tools.AddRange(values())
                    Case "--strict-mcp-config" : o.StrictMcpConfig = True
                    Case "--fork-session" : o.ForkSession = True
                    Case "--no-session-persistence" : o.NoSessionPersistence = True
                    Case "--replay-user-messages" : o.ReplayUserMessages = True
                    Case "--disable-slash-commands" : o.DisableSlashCommands = True
                    Case "--fallback-model" : o.FallbackModel = valueOf()
                    Case "-w", "--worktree"
                        ' The name is optional: the next word is taken only when it looks like one (no spaces, not an option).
                        If inline IsNot Nothing Then
                            o.Worktree = inline
                        ElseIf i + 1 < args.Length AndAlso Text.RegularExpressions.Regex.IsMatch(args(i + 1), "^[A-Za-z0-9._/-]{1,60}$") AndAlso Not args(i + 1).StartsWith("-") Then
                            i += 1
                            o.Worktree = args(i)
                        Else
                            o.Worktree = ""
                        End If
                    Case "-i", "--image"
                        If inline IsNot Nothing Then o.Images.Add(inline) Else o.Images.AddRange(values())
                    Case "--max-budget-usd"
                        Dim usd As Decimal
                        If Decimal.TryParse(valueOf(), Globalization.NumberStyles.Number, Globalization.CultureInfo.InvariantCulture, usd) AndAlso usd > 0 Then o.MaxBudgetUsd = usd Else o.Errors.Add("--max-budget-usd needs a positive amount")
                    Case "--add-dir"
                        If inline IsNot Nothing Then o.AddDirs.Add(inline) Else o.AddDirs.AddRange(values())
                    Case "--mcp-config"
                        If inline IsNot Nothing Then o.McpConfigs.Add(inline) Else o.McpConfigs.AddRange(values())
                    Case "--settings" : o.SettingsFile = valueOf()
                    Case "--max-turns"
                        Dim n As Integer
                        If Integer.TryParse(valueOf(), n) Then o.MaxTurns = n
                    Case "--verbose" : o.Verbose = True
                    Case "--debug", "-d" : o.Debug = True
                    Case "--effort", "--reasoning-effort" : o.Effort = valueOf()
                    Case "--web" : o.Web = True
                    Case "--no-web" : o.Web = False
                    Case "--shell" : o.Shell = valueOf()
                    Case "--no-color" : o.NoColor = True
                    Case "-h", "--help" : o.Help = True
                    Case "-v", "--version" : o.Version = True
                    Case "--selftest" : o.SelfTest = True
                    Case "--setup" : o.Command = "login"
                    Case Else
                        If a.StartsWith("-") AndAlso a.Length > 1 AndAlso positional.Count = 0 Then
                            o.Errors.Add("Unknown option " & a)
                        Else
                            positional.Add(a)
                        End If
                End Select
                i += 1
            End While
            If positional.Count > 0 AndAlso o.Command Is Nothing AndAlso Subcommands.Contains(positional(0).ToLowerInvariant()) AndAlso Not o.Print Then
                o.Command = positional(0).ToLowerInvariant()
                o.CommandArgs = positional.Skip(1).ToList()
            ElseIf positional.Count > 0 Then
                o.Prompt = String.Join(" ", positional)
            End If
            Return o
        End Function

        Private Shared Function JoinPrompt(existing As String, more As String) As String
            If String.IsNullOrEmpty(more) Then Return existing
            Return If(String.IsNullOrEmpty(existing), more, existing & Environment.NewLine & Environment.NewLine & more)
        End Function

        ''' <summary>The text of --system-prompt-file / --append-system-prompt-file (an error when it cannot be read).</summary>
        Private Shared Function ReadPromptFile(file As String, o As CliOptions) As String
            If String.IsNullOrWhiteSpace(file) Then Return Nothing
            Try
                Return IO.File.ReadAllText(file)
            Catch ex As Exception When TypeOf ex Is IO.IOException OrElse TypeOf ex Is UnauthorizedAccessException
                o.Errors.Add($"Cannot read {file}: {ex.Message}")
                Return Nothing
            End Try
        End Function

        Private Shared Function LooksLikeId(s As String) As Boolean
            Return s.Length >= 6 AndAlso Not s.Contains(" "c) AndAlso s.All(Function(c) Char.IsLetterOrDigit(c) OrElse c = "_"c OrElse c = "-"c)
        End Function

        ''' <summary>Splits "Bash(git:*),Edit Read" into rules, keeping spaces and commas inside parentheses.</summary>
        Public Shared Function SplitList(s As String) As List(Of String)
            Dim list As New List(Of String)
            Dim cur As New Text.StringBuilder()
            Dim depth = 0
            For Each c In If(s, "")
                If c = "("c Then depth += 1
                If c = ")"c Then depth = Math.Max(0, depth - 1)
                If depth = 0 AndAlso (c = ","c OrElse c = " "c) Then
                    If cur.ToString().Trim().Length > 0 Then list.Add(cur.ToString().Trim())
                    cur.Clear()
                Else
                    cur.Append(c)
                End If
            Next
            If cur.ToString().Trim().Length > 0 Then list.Add(cur.ToString().Trim())
            Return list
        End Function

        Public Shared ReadOnly Property Usage As String
            Get
                Return "Usage: agentic [options] [prompt]
       agentic <command> [args]

Starts an interactive session in this folder (with an optional first prompt).

Options:
  -p, --print                      Answer once and exit (reads stdin too); for scripts and pipes
  --output-format <f>              text | json | stream-json (with --print)
  --input-format stream-json       Be driven by another program: JSON messages on stdin, JSON events on stdout
                                   (multi-turn, interrupts, permission questions, the host's own tools; see README)
  --permission-prompt host|deny    With stream-json input: send permission questions to the host (default) or refuse
  -c, --continue                   Continue the most recent conversation in this folder
  -r, --resume [id]                Resume a conversation (pick one when no id is given)
  -m, --model <id>                 Model for the session (a custom model or a provider model)
  --permission-mode <m>            default | acceptEdits | plan | bypassPermissions
  --dangerously-skip-permissions   Run every tool without asking (bypassPermissions)
  --allowedTools <rules...>        Allow without asking, e.g. ""Bash(git log:*)"" Edit Read
  --disallowedTools <rules...>     Never allow, e.g. ""Bash(rm:*)"" WebFetch
  --append-system-prompt <text>    Add to the instructions
  --system-prompt <text>           Replace the agent's role description
  --system-prompt-file <file>      --system-prompt, read from a file
  --append-system-prompt-file <f>  --append-system-prompt, read from a file
  -n, --name <title>               Title of the new conversation
  --fork-session                   With -c / -r: continue in a copy, the original stays as it was
  --no-session-persistence         A temporary conversation, not kept in the history
  --tools <names...>               Only these built-in tools (e.g. Read Grep Glob); """" for none
  --strict-mcp-config              Only the MCP servers given with --mcp-config
  --fallback-model <id>            Model to use when the main one is overloaded or unavailable
  --max-budget-usd <n>             Stop once the session's model calls cost this much (with --print)
  --disable-slash-commands         No custom commands or skills
  --replay-user-messages           stream-json: echo each user message back on stdout
  --add-dir <dirs...>              More folders the tools may work in
  -w, --worktree [name]            Work in a git worktree: its own folder (.agentic\worktrees\<name>) and branch
  -i, --image <files...>           Images to attach to the first message
  --mcp-config <files|json...>     Extra MCP servers (Claude Code mcpServers format)
  --settings <file|json>           Extra settings
  --max-turns <n>                  Stop after n tool rounds per message (default: no fixed limit, it works while it makes progress)
  --effort <e>                     none | low | medium | high | ultracode
  --web / --no-web                 Web search on or off
  --shell <s>                      powershell | pwsh | cmd | bash for the Bash tool
  --verbose                        Show full tool output and thinking
  --no-color                       No colours
  -v, --version                    Print the version
  -h, --help                       This help

Commands:
  login                            Connect to your Agentic server (address and API key)
  logout                           Forget the stored API key
  auth status|login|logout         Who you are signed in as on the server (and log in or out)
  doctor                           Check the connection, tools and configuration
  update [--check]                 Install the server's newer build now (sessions also update in the background)
  mcp list|add|add-json|get|remove|enable|disable|import
                                   Manage MCP servers (agentic mcp help)
  config list|get|set|add|remove   Read and change settings (agentic config help)
  sessions                         Conversations started in this folder
  agents | skills                  List subagents / skills and commands

In a session type /help for commands. Docs: README.md next to the program."
            End Get
        End Property

    End Class

End Namespace
