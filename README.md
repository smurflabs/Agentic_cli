# Agentic CLI

A coding agent in your terminal, like Claude Code, Cline or OpenClaw. It runs on your Agentic server, so it uses the server's models: custom routing models, provider models, web search, server MCP servers and the code interpreter. The tools themselves run on your computer:

- files: read, write, edit
- search
- shell commands
- web pages
- subagents
- skills
- MCP servers

```
agentic                          start a session in this folder
agentic "fix the failing tests"  start with a first message
agentic -c                       continue the last conversation in this folder
agentic -p "explain src/app.vb"  answer once and exit (scripts, pipes, CI)
agentic --input-format stream-json   be driven by another program over stdin/stdout (see below)
```

The CLI starts with the model you last used on this account, in the CLI, the web app or the desktop app.

**Setting it up:** [SETUP.md](SETUP.md) covers installing, building from source and signing in to your server.

## Install

1. Build it, or use the published `publish\agentic.exe` (a single file). On Linux, download `Agentic-CLI-<version>-linux-x64.tar.gz` from **Apps & updates** in the web app and run `tar -xzf` on it, which gives `Agentic-CLI/agentic`.
2. Put the folder on your `PATH`. On Linux you can link the binary instead: `ln -s "$PWD/Agentic-CLI/agentic" ~/.local/bin/agentic`. Keep the folder owned by you, because the CLI replaces its own file when it updates.
3. Run `agentic login`. It asks for:
   - the server: `https://agentic.smurflabs.xyz` is chosen by default; pick **Another address…** to type your own
   - a personal API key: in the web app, open the user menu → **API key** → **Generate**

Both are saved in `config.json` in the folder `agentic.exe` (`agentic` on Linux) runs from, so you can also write that file yourself:

```json
{
  "serverBaseUrl": "https://agentic.smurflabs.xyz",
  "apiKey": "agk_..."
}
```

The key is stored there as plain text. Anyone who can read that folder can use it, so keep the CLI in a folder only you can read. For CI, set `AGENTIC_SERVER` and `AGENTIC_API_KEY` instead; they override `config.json`. `agentic logout` clears the key and keeps the address.

Run `agentic doctor` to check the connection, your account, the models, the tools on this computer and your configuration.

## A session

You type in the box at the bottom. The answer streams above it, with Markdown, code highlighting and tables. Tool calls are shown the way Claude Code shows them: `● Update(src/app.vb)`, with a `⎿` result line and a coloured diff.

| Keys | |
|---|---|
| Enter | Send |
| `\` + Enter, Ctrl+J, Shift+Enter | New line. Pasting keeps line breaks; a big paste becomes `[Pasted text #1 +40 lines]` |
| `/` | Commands; Tab or Enter completes |
| `@path` | Attach a file or folder. Tab completes. Images are uploaded for the model to see |
| `! command` | Run a command here. Its output goes with your next message |
| `# note` | Save a note to a memory file (AGENTS.md) |
| Shift+Tab | Cycle the mode: normal → accept edits → plan |
| Esc | Stop the answer. Esc twice clears the box; on an empty box it goes back to an earlier message |
| ↑ / ↓, Ctrl+R | Input history and history search (kept per project) |
| Ctrl+O | Verbose: full tool output and the model's thinking |
| Ctrl+T | Show the todo list |
| Ctrl+G | Edit the message in your editor (`$VISUAL` / `$EDITOR`, else Notepad or nano); what you save comes back into the box |
| Alt+V | Paste an image from the clipboard (a screenshot, a copied picture or image file): it shows as `[Image #1]` and is attached when you send |
| Ctrl+Z | Undo in the box |
| Ctrl+L | Clear the screen |
| Ctrl+C twice, Ctrl+D | Exit |

These are the defaults: **every key can be changed with `/keys`** — pick an action and press the new key (two keys one
after the other, like Esc Esc, also work), or `/keys set verbose F2`, `/keys set submit Ctrl+Enter` (Enter then adds a line),
`/keys reset all`. Moving, deleting, menus, the history search and the keys used while an answer runs can be changed too.
Changes are saved in `~/.agentic/settings.json` under `keybindings` and stay after a restart.

While an answer runs you can type your next message and press Enter; it is sent when the answer ends.

### Commands

| Command | |
|---|---|
| `/help` | Commands and keys |
| `/clear [name]` | New conversation; a name labels the one you leave |
| `/compact [focus]` | Summarize older messages to free context, optionally focused on something |
| `/branch [name]` (`/fork`) | Copy the conversation as it is now and continue in the copy; the original stays |
| `/btw <question>` | A side question about the session; the answer is not added to the conversation |
| `/recap` | One line: what the session is about and where it stands |
| `/diff [path]` | The working tree's changes as git sees them |
| `/cd <path>` | Move the session to another folder, keeping the conversation |
| `/context` | How full the context window is (instructions, tools, messages) |
| `/cost` | Tokens and time used in this session |
| `/usage` (`/stats`) | Your account's use in every app and the API: tokens and calls today, this month and over 30 days, by model and by day; the balance and the limits an administrator set |
| `/find <text>` | Find in this conversation: messages, tool output and thinking (`#n` is the message's number) |
| `/model [name]` | Choose a model |
| `/effort` | Reasoning effort: none, low, medium, high, ultracode |
| `/mode`, `/plan` | Permission mode and plan mode |
| `/permissions` | Show or change allow, ask and deny rules |
| `/resume`, `/continue`, `/rename` | Earlier conversations |
| `/rewind` | Go back to before a message: restore the files, the conversation, or both |
| `/export [file\|clipboard]`, `/copy` | The conversation as Markdown; the last answer to the clipboard |
| `/init` | Analyze the codebase and write AGENTS.md |
| `/memory` | Open a memory file in your editor |
| `/add-dir <path>` | Let the tools work in another folder |
| `/agents`, `/skills`, `/mcp`, `/hooks` | Subagents, skills and commands, MCP servers, hooks |
| `/parallel [on\|off\|N]` | Parallel agents: the model splits independent work into Task subagents that run at the same time (N = most at once, 1-16; remembered as `parallelAgents` / `maxParallelAgents`) |
| `/config`, `/status`, `/doctor` (`/checkup`) | Settings, setup overview, checks |
| `/statusline [command\|off]` | The command whose output shows in the status line |
| `/reload-skills`, `/release-notes` | Pick up new skills and commands; what changed in the newest CLI |
| `/todos`, `/bashes` (`/tasks`) | The todo list; background commands |
| `/monitors [stop <#>\|all]` | Commands the `Monitor` tool watches: their output lines arrive as messages while Agentic waits for you |
| `/read [on\|off]` | Read the last answer aloud in your voice, as the server's speech server makes it (code and links are left out; Esc stops it). `on`: every answer is read aloud (the `readAloud` setting) |
| `/tldr` | Write the last answer's short spoken summary in the voice's personality (kept with the answer) and speak it |
| `/voice [name]` | The voice that reads answers aloud and talks in Dictation mode (no name: a menu of the built-in and cloned voices; the same choice as the web app's `/voice`) |
| `/dictation [on\|off]` (`/talk`) | Dictation mode: talk instead of typing. It listens until you pause (your pause, length and sensitivity settings from the web app's Audio → Dictation), the server turns it into text, it is sent, and each answer's short spoken summary is spoken; then it listens again. Enter sends at once, Esc goes back to typing. Saying only "stop", "that's enough for now", "thanks, that's all" or "goodbye" ends it and sends nothing; "stop the server" is still sent as a message. Sound: Windows needs nothing; Linux uses `paplay`/`aplay` and `parecord`/`arecord` (or `sox`, `ffmpeg`); macOS `sox` or `ffmpeg`. `dictationLanguage` sets the language heard (default: `LANG`, else English) |
| `/schedule [<when> <prompt>\|list\|all\|pause\|resume\|delete\|run <#>]` | Prompts the server sends to this conversation on a schedule (`every 30m`, `hourly`, `daily 09:00`, `weekdays 08:30`, `at 14:00`, `once 2026-11-01 09:00`), also while the CLI is closed |
| `/run [name\|#\|stop\|status\|log\|open\|share\|edit]` | Run or test the project (and folders added with `/add-dir`), like the desktop app's Run button: a menu of the ways to run it — `.agentic/launch.json` or Claude Code's `.claude/launch.json`, else its files (package.json scripts, a static site on a built-in server, Python, .NET, Go, Rust, a Makefile). A dev server's address is printed once it answers (`/run open` shows it in the browser); tests and apps show their output as it comes and how they ended (Esc stops following; it keeps running). `/run share` uploads the project's website (its build or a static site) and prints a public link; `/run edit` writes and opens `.agentic/launch.json` |
| `/review [PR]` (`/code-review`), `/security-review` | Review the pending changes or a pull request |
| `/pr-comments [PR]` | Work through a pull request's review comments (gh, or tea / the host's API for Gitea) and fix them |
| `/ide [list\|off\|<n>]` | Connect to VS Code (Agentic for VS Code 0.2.24+, in the same folder): your selection (or the open file) goes with each message, file edits show as diffs in VS Code to accept, reject or accept in part, and the `IdeDiagnostics` tool reads VS Code's problems. In VS Code's own terminal it connects by itself (`ide.autoConnect`) |
| `/plugin [list\|enable\|disable\|install\|uninstall\|browse\|marketplace add\|remove\|update\|list]` (`/plugins`) | Claude Code plugins: their commands (`/plugin:command`), skills, agents (`plugin:agent`), hooks and MCP servers work here. Turning one on or off here does not change Claude Code; a Claude Code plugin's hooks run once you turn it on here. `marketplace add` takes `owner/repo`, a git URL, a folder or a marketplace.json URL; installs go to `~\.agentic\plugins` |
| `/feedback [what happened]` (`/bug`) | Send a report to the administrators' Inbox with this conversation and the CLI's setup; their reply shows with `/feedback` |
| `/simplify`, `/verify` | Simplify the pending changes without changing what they do; build, test and run the project to check that they work |
| `/research <question>` | Deep research on the server: several web searches, sources read and cross-checked, a report that cites them (turns web search on) |
| `/worktree [name\|list\|done\|remove <name>]` | Work in a git worktree: its own folder (`.agentic\worktrees\<name>`) on its own branch (`agentic/<name>`), so changes stay out of the main checkout until you merge them. `done` goes back |
| `/web`, `/code` | The server's web search and code interpreter |
| `/output-style` | default, explanatory, learning, concise |
| `/vim` | Vim keys in the message box (saved as `editorMode`) |
| `/keys` | See and change every key (`list`, `set <action> <keys>`, `reset <action>\|all`) |
| `/loop [interval] <prompt>` | Runs a prompt or a `/command` every few minutes while the session waits for you (`30s`, `5m`, `2h`; default 10m). `/loop` lists the loops, `/loop stop [#\|all]` ends them |
| `/heartbeat [on [interval]\|off\|now]` | Checks HEARTBEAT.md on a schedule (default every 30m) |
| `/share [off]` | A read-only web link to this conversation; it is also copied to the clipboard |
| `/memories [on\|off\|clear]`, `/remember <fact>`, `/forget <id or words>` | What the assistant remembers about you. The memory is the server's, so it is shared with the web and desktop apps and used in every chat |
| `/prompts` | Your saved prompts from the web app |
| `/verbose`, `/login`, `/logout`, `/exit` | |

Your own commands and skills appear in the same list (see below), and so do your saved prompts: `/name text` sends the prompt followed by the text.

**Vim mode** (`/vim`):

- Esc switches to normal mode, and `-- NORMAL --` shows in the footer.
- Motions: `h j k l w b e 0 ^ $ gg G`.
- Editing: `x X D C S s dd dw de db d$ cc cw yy yw p P u`.
- Back to typing: `i a I A o O`.
- Enter sends in both modes.

**Loops and the heartbeat** run only while the message box is empty and no answer is running; typing pauses them. They last for the session.

- A loop's run shows as `⟳ /loop #1 …` instead of an echoed message.
- **HEARTBEAT.md:** the heartbeat reads it from the project folder (or `.agentic\HEARTBEAT.md`, or `~\.agentic\HEARTBEAT.md`). Write it as a checklist; headings and `<!-- comments -->` are ignored.
- The model acts on anything that needs attention, and answers just `HEARTBEAT_OK` when nothing does.
- `/heartbeat on` creates a starter file when there is none.

## Tools

The model gets these tools, which run on your computer:

| Tool | Does |
|---|---|
| Read, Write, Edit, MultiEdit | Read files (with line numbers), create them, change exact text. Encoding, BOM and line endings are kept. A file must be read before it is changed, and is refused if it changed since |
| Glob, Grep, LS | Find files by pattern; search contents with regular expressions (context lines, counts, type and glob filters). `.gitignore` and build folders are skipped |
| Bash, BashOutput, KillShell | Run commands in PowerShell (default), pwsh, cmd or Git Bash (`shell` setting). `cd` carries over between commands. A command still running after its `timeout` is not stopped: it goes on in the background (BashOutput audits it: status, run time, time since the last output, warnings when it seems stuck or waits for input, `wait_seconds` to wait; KillShell stops it with what it started) |
| WebFetch | A web page as readable text |
| TodoWrite | The task list for multi-step work |
| Task | A subagent with its own conversation and tools. Several Task calls in one response run at the same time (`/parallel`); their steps are tagged with their names, and read-only calls of one round (Read, Grep, Glob…) run together too. `fork: true` starts it with this conversation; subagents can start their own, two levels deep |
| Monitor | Runs a command in the background and streams its output lines (those matching a filter) into the conversation as they happen: they arrive as a message while Agentic waits for you, so the model reacts to a log, a build or a server. Bash permission rules cover it |
| ExitPlanMode | In plan mode, asks you to approve the plan |
| Skill, SkillFile | Load a skill's instructions and files |
| `mcp__server__tool` | Tools of your MCP servers |
| ask_user, web search, code interpreter, server MCP | Run on the Agentic server |

## Permissions

Reading and searching inside the working folders runs without asking. Everything else follows the mode and your rules.

| Mode | |
|---|---|
| default | Asks before file edits, commands that change things, web fetches and MCP tools. Read-only commands such as `git status`, `ls` and `Get-Content` run without asking |
| acceptEdits | File edits in the working folders run without asking |
| plan | Read-only research, then a plan to approve (ExitPlanMode). Approving it lets the agent implement it |
| bypassPermissions | Everything runs (`--dangerously-skip-permissions`). Deny rules still apply |

When it asks, the answers are:
- **Yes**
- **Yes, and don't ask again**: saves a rule in `.agentic\settings.local.json`
- **No, and tell Agentic what to do differently**

Rules use Claude Code's syntax. Deny rules always win:

```json
{
  "permissions": {
    "allow": ["Bash(npm run test:*)", "Bash(git log:*)", "Edit(src/**)", "WebFetch(domain:learn.microsoft.com)", "mcp__github"],
    "ask":   ["Bash(git push:*)"],
    "deny":  ["Read(**/.env)", "Bash(rm -rf:*)"],
    "defaultMode": "default",
    "additionalDirectories": ["C:\\shared\\lib"]
  }
}
```

How patterns match:
- `Bash(prefix:*)`: commands starting with that prefix. Chained commands (`&&`, `;`, `|`) must match in every part.
- Path patterns: relative to the project. `//C:/x/**` or `C:\x\**` is absolute, `~/x` is in your home folder, and `*.env` matches anywhere.
- An `Edit(...)` rule covers Write, MultiEdit and NotebookEdit too.
- A `Read(...)` rule covers Glob, Grep and LS too.

You can also give rules on the command line: `--allowedTools "Bash(git:*)" Edit` and `--disallowedTools WebFetch`.

## Settings

Settings files are merged in this order; a later file wins, and lists are combined:

| File | |
|---|---|
| `~\.agentic\settings.json` | Yours, for all projects |
| `.agentic\settings.json` | The project's (commit it) |
| `.agentic\settings.local.json` | Yours, for this project (git-ignored automatically) |
| `--settings <file or JSON>` | For one run |

| Key | |
|---|---|
| `model`, `effort`, `outputStyle` | Defaults for the session |
| `permissions` | See above |
| `hooks` | See below |
| `env` | Environment variables for commands and hooks |
| `shell` | `auto` (pwsh, else Windows PowerShell), `powershell`, `pwsh`, `cmd` or `bash` |
| `maxTurns` | How often the server checks an answer's progress, in tool rounds (default 80, up to 200). Not a stop: an answer that keeps getting things done goes on; one whose last stretch brought nothing new is asked to answer. `--max-turns <n>` is a stop at n. |
| `webSearch`, `codeInterpreter`, `serverMcpServers` | Server features (off by default) |
| `showThinking`, `verbose`, `notifications` | `notifications` is `auto` (default: the bell, and a desktop notification when the terminal is in the background), `bell` or `off` |
| `statusLine` | `{"type":"command","command":"…"}`: its first line replaces the right side of the status line. It gets the session as JSON on stdin |
| `includeClaudeConfig` | Also read Claude Code's files: CLAUDE.md and the `.claude` skills, commands and agents (default true) |
| `readAloud`, `dictationLanguage` | Read every answer aloud (`/read on`); the language Dictation mode hears (e.g. `de`). For tests or a machine without sound, the environment variables `AGENTIC_AUDIO_OUT` (a file the speech is written to, raw 24 kHz PCM) and `AGENTIC_AUDIO_IN` (a WAV file heard as the microphone) stand in for the speaker and microphone |

Manage them with `/config` in a session, or `agentic config list|get|set|add|remove`.

## Memory: instructions the model always gets

| File | |
|---|---|
| `AGENTS.md`, `AGENTIC.md`, `CLAUDE.md`, `.agentic\AGENTS.md`, `.claude\CLAUDE.md` | In the project and its parent folders |
| `AGENTS.local.md`, `CLAUDE.local.md` | Yours only |
| `~\.agentic\AGENTS.md` (and `~\.claude\CLAUDE.md`) | For all your projects |
| `.clinerules`, `.cursorrules`, `.github\copilot-instructions.md` | Other tools' rules |

A line `@path\to\file.md` imports another file. `/init` writes an AGENTS.md for the project, `# note` adds a line, and `/memory` opens one in your editor.

## Commands, skills and subagents (Claude Code format)

Your own commands, skills and subagents all live in Markdown files. The `.claude` folders of Claude Code work too.

- **Commands**: `.agentic\commands\name.md` or `~\.agentic\commands\name.md`. Run one as `/name args`.
  - Front matter: `description`, `argument-hint`.
  - The body is the prompt. `$ARGUMENTS` and `$1`, `$2` are replaced. `` !`git diff` `` inserts that command's output.
- **Skills**: `.agentic\skills\name\SKILL.md` or `~\.agentic\skills\…`.
  - Front matter: `name`, `description`.
  - The model loads a skill when your request matches its description; `/name` runs it directly.
  - Skills from Claude Code plugins and from the Agentic desktop app are included.
- **Subagents**: `.agentic\agents\name.md` or `~\.agentic\agents\…`.
  - Front matter: `name`, `description`, `tools` (comma-separated), `model`.
  - The model starts them with the Task tool.
  - Built in: `general-purpose`, `Explore` (read-only, fast) and `Plan`.
  - Create one with `/agents new`.

## Hooks

Hooks use Claude Code's format and events:
- PreToolUse, PermissionRequest, PostToolUse, PostToolUseFailure
- UserPromptSubmit
- Stop, StopFailure, SubagentStart, SubagentStop
- SessionStart, SessionEnd
- Notification, PreCompact, PostCompact, CwdChanged

```json
{ "hooks": { "PostToolUse": [ { "matcher": "Edit|Write|MultiEdit", "hooks": [ { "type": "command", "command": "dotnet format --include $AGENTIC_PROJECT_DIR", "timeout": 60 } ] } ] } }
```

How a hook runs:
- It gets the event as JSON on stdin.
- **Exit code 2 blocks the action**, and stderr goes to the model:
  - PreToolUse: the tool is refused.
  - Stop: the agent keeps working on stderr's instructions.
  - UserPromptSubmit: the message is not sent.
- **Exit code 0 with JSON** output can decide:
  - `{"decision":"block","reason":…}`
  - `{"hookSpecificOutput":{"permissionDecision":"allow|deny|ask","additionalContext":…}}`
  - PreToolUse `{"hookSpecificOutput":{"updatedInput":{…}}}`: the tool runs with these arguments instead
  - PermissionRequest `{"hookSpecificOutput":{"decision":{"behavior":"allow|deny","message":…}}}`: answers before you are asked
  - `{"continue":false}`
- For UserPromptSubmit and SessionStart, plain stdout is added as context.

## MCP servers

```
agentic mcp add files -- npx -y @modelcontextprotocol/server-filesystem C:\work
agentic mcp add --transport http github https://api.githubcopilot.com/mcp/ -H "Authorization: Bearer …"
agentic mcp add-json db '{"command":"uvx","args":["mcp-server-sqlite","--db-path","app.db"]}'
agentic mcp list | get | remove | enable | disable | test <name>
agentic mcp import          copy servers from Claude Code, Claude Desktop or the Agentic desktop app
agentic mcp login <name>    sign in to a hosted server (OAuth in the browser); logout forgets it
agentic mcp serve           this CLI's tools (Read, Edit, Bash …) as an MCP server for other apps
```

Where servers are configured:
- `~\.agentic\mcp.json` for you.
- `.mcp.json` in the project, with `-s project`. Each project server is approved once per person.
- `--mcp-config` for one run.
- Without importing anything:
  - your Claude Code servers (`~\.claude.json`, for you and for this project);
  - your Claude Code plugins' servers;
  - Claude Desktop's servers.

  `includeClaudeConfig: false` turns this off. `/mcp disable <name>` switches one off here and leaves its file as it is.

Values can use `${VAR}` and `${VAR:-default}` (Claude Code's syntax) or `%VAR%`. `envFile` loads a .env file.

Transports: stdio (including npx and uvx), Streamable HTTP and SSE.
- A hosted server that needs a sign-in says so in `/mcp`. `/mcp login <name>` signs in with OAuth in the browser.
- Tokens are kept encrypted for your Windows account in `~\.agentic\mcp-auth.bin`.
- In a session, `/mcp` shows each server's status and tools. The Agentic server's own MCP servers are listed there too and can be switched on.

The model also gets, from MCP servers:
- pictures (a browser's screenshots);
- their resources (ListMcpResources, ReadMcpResource);
- their prompts, as `/mcp__server__prompt` commands.

A server that asks you something while a tool runs asks in the terminal.

## Print mode (scripts and CI)

```
agentic -p "list the TODO comments in src" --output-format json
git diff | agentic -p "write a commit message for this diff"
agentic -p "fix the lint errors" --permission-mode acceptEdits --allowedTools "Bash(npm run lint:*)" --max-turns 30
```

Work in a git worktree with `-w/--worktree [name]`, and attach images to the first message with `-i/--image <files...>`.

More options, the same as Claude Code's: `--system-prompt-file`, `--append-system-prompt-file`, `-n/--name`, `--fork-session` (with `-c`/`-r`), `--no-session-persistence`, `--tools Read Grep` (only these built-in tools), `--strict-mcp-config`, `--fallback-model`, `--max-budget-usd`, `--disable-slash-commands` and `--replay-user-messages`. `agentic auth status` shows who you are signed in as.

In print mode nothing can be asked: actions that would need your answer are refused, and the model is told so. Allow them with:
- `--allowedTools`
- `--permission-mode acceptEdits`
- `--dangerously-skip-permissions`

Output formats:

| Format | |
|---|---|
| `text` | The answer only |
| `json` | One object with `result`, `session_id`, `usage`, `num_turns`, `stop_reason` and `is_error` |
| `stream-json` | One JSON event per line: `system`, `assistant`, `tool_use`, `tool_result`, `result` |

## Driving the CLI from your own programs

Any program can start `agentic.exe` and control it: it gets the full agent (models, tools, MCP, skills, memory, permissions) without talking to the server itself.

```
agentic --input-format stream-json [--model <id>] [--permission-mode <mode>] [--allowedTools ...] [--permission-prompt host|deny]
```

The connection works like this:
- Your program writes one JSON object per line on the CLI's **stdin** and reads one JSON event per line from its **stdout**. Nothing else is written to stdout.
- The process stays up for as many turns as you like. Messages sent while a turn runs are queued.
- Closing stdin ends the process once the current turn is done.
- The exit code is 1 if any turn failed, 0 otherwise.
- Login is the same as in a session: `config.json` next to `agentic.exe` (written by `agentic login`), or the `AGENTIC_SERVER` and `AGENTIC_API_KEY` environment variables.

### What your program sends (stdin)

| Message | Meaning |
|---|---|
| `{"type":"user","message":"..."}` | Start a turn. `message` can also be `{"content":"..."}` or Claude-style `{"content":[{"type":"text","text":"..."}]}`. |
| `{"type":"register_tools","tools":[{"name","description","parameters","readOnly"}]}` | Give the model your own tools. `parameters` is a JSON schema; OpenAI `{"type":"function","function":{...}}` entries work too. Each call replaces the earlier list. These tools run without asking. |
| `{"type":"tool_result","id":"host_…","ok":true,"content":"..."}` | The answer to a `host_tool_call`. `content` can be a string or any JSON; `ok:false` reports an error to the model. |
| `{"type":"permission_response","id":"perm_…","allow":true,"remember":false,"message":"..."}` | The answer to a `permission_request`. `remember:true` allows the same kind of action for the rest of the process. With `allow:false`, `message` tells the model why. |
| `{"type":"control","action":"interrupt"}` | Stop the current turn. Waiting questions are answered "no". |
| `{"type":"control","action":"set_model","model":"<id>"}` | Switch the model. The server remembers it as your last model. |
| `{"type":"control","action":"set_permission_mode","mode":"acceptEdits"}` | `default`, `acceptEdits`, `plan` or `bypassPermissions`. |
| `{"type":"control","action":"set_effort","effort":"high"}` | Reasoning effort. |
| `{"type":"control","action":"new_conversation"}` | The next message starts a new chat. |
| `{"type":"control","action":"resume","session_id":"chat_…"}` | Continue an earlier chat. |

### What the CLI sends (stdout)

| Event | Meaning |
|---|---|
| `{"type":"system","subtype":"init","protocol":1,"version","cwd","model","permissionMode","session_id","tools"}` | The first line. Send `register_tools` and your first message after it. |
| `{"type":"assistant","text":"..."}` | The answer as it is written, in pieces. |
| `{"type":"tool_use","tool":{"id","name","input"}}` and `{"type":"tool_result","result":{"id","name","ok","content"}}` | A tool the model used, and what it returned. |
| `{"type":"permission_request","id","tool","input","title","reason"}` | The model wants to do something that needs approval. The turn waits for your `permission_response`. |
| `{"type":"host_tool_call","id","name","input"}` | The model called one of your tools. The turn waits for your `tool_result`. |
| `{"type":"result","subtype","is_error","result","error","session_id","duration_ms","num_turns","stop_reason","usage","questions"}` | The turn is over. `subtype` is `success`, `error` or `interrupted`. |
| `{"type":"control_response","action","ok","error","model","permissionMode","session_id"}` | The answer to a `control` or `register_tools` message. |
| `{"type":"error","message"}` | A line the CLI could not use. The process keeps running. |

Permission questions follow the same rules as in a session:
- `--allowedTools` / `--disallowedTools`, settings files and the permission mode decide first.
- Only what is still undecided reaches your program.
- With `--permission-prompt deny`, those questions are refused instead, as in print mode.

### Example (Node.js)

```js
const { spawn } = require('child_process');
const readline = require('readline');
const cli = spawn('agentic', ['--input-format', 'stream-json']);
const send = (m) => cli.stdin.write(JSON.stringify(m) + '\n');

readline.createInterface({ input: cli.stdout }).on('line', (line) => {
  const e = JSON.parse(line);
  switch (e.type) {
    case 'system':
      send({ type: 'register_tools', tools: [{ name: 'get_build_number', description: 'Returns the build number of this app.',
                                                 parameters: { type: 'object', properties: {} }, readOnly: true }] });
      send({ type: 'user', message: 'What build is this? Then add a CHANGELOG.md entry for it.' });
      break;
    case 'host_tool_call': send({ type: 'tool_result', id: e.id, ok: true, content: 'Build 4711' }); break;
    case 'permission_request': send({ type: 'permission_response', id: e.id, allow: e.tool !== 'Bash' }); break;
    case 'assistant': process.stdout.write(e.text); break;
    case 'result': console.log('\n[done]', e.subtype); cli.stdin.end(); break;
  }
});
```

### Example (VB.NET)

```vb
Dim psi As New ProcessStartInfo("agentic.exe", "--input-format stream-json") With {
    .RedirectStandardInput = True, .RedirectStandardOutput = True, .UseShellExecute = False, .StandardOutputEncoding = Text.Encoding.UTF8}
Using p = Process.Start(psi)
    Dim send = Sub(o As Object) p.StandardInput.WriteLine(Text.Json.JsonSerializer.Serialize(o))
    Do
        Dim line = p.StandardOutput.ReadLine()
        If line Is Nothing Then Exit Do
        Dim e = Text.Json.JsonDocument.Parse(line).RootElement
        Select Case e.GetProperty("type").GetString()
            Case "system" : send(New With {.type = "user", .message = "Summarise README.md in three bullet points."})
            Case "assistant" : Console.Write(e.GetProperty("text").GetString())
            Case "permission_request" : send(New With {.type = "permission_response", .id = e.GetProperty("id").GetString(), .allow = False})
            Case "result" : p.StandardInput.Close()
        End Select
    Loop
End Using
```

## Where things are

| Path | |
|---|---|
| `config.json` next to `agentic.exe` | Server address (`serverBaseUrl`) and API key (`apiKey`) |
| `%APPDATA%\AgenticCli\secrets.bin` | Where older versions kept the key (DPAPI); still read when `config.json` has none, deleted on the next login |
| `~\.agentic\` | settings.json, mcp.json, AGENTS.md, commands, skills, agents |
| `~\.agentic\projects\<project>\` | Conversations started in the project (`-c`, `/resume`), input history, checkpoints for `/rewind` |

Conversations are stored on the server: they appear in the web app's Chat tab, and the web app can follow an answer live. Subagent conversations are archived. Set `AGENTIC_HOME` to keep all of this in another folder.

## Updates

The CLI keeps itself up to date from the Agentic server (**Apps & updates** in the web app).

- **When it checks:** when an interactive session starts, in the background. When a newer build is found:
  1. The CLI downloads it and checks it against the server's SHA-256.
  2. It puts the new file in place of `agentic.exe` (`agentic` on Linux, where the new file keeps the old one's file mode). The running copy is renamed to `agentic.exe.old-…` and deleted at the next start.
  3. The footer shows *updated to … · restart agentic to use it*.
- **`agentic update`:** installs a newer build now. `agentic update --check` only reports whether there is one, and `agentic doctor` shows the update status.
- **What counts as newer:** a higher version, or the same version with another SHA-256 (a rebuild).
- **Turning it off:** set `"autoUpdates": false` in settings, or `DISABLE_AUTOUPDATER=1`.
- **When it does not run:** print mode and stream-json never update. A development build (`bin\Debug`, with `agentic.dll` next to it) never updates either.

- **Each platform has its own build:** the Windows build asks the server for `cli`, and the Linux build asks for `cli-linux-x64`.

To publish a new version, raise `<Version>` in `AgenticCli.vbproj`, then run `tools\publish-clients.ps1 -Apps cli` in the Agentic server repository. It builds the Windows and the Linux build.

## Build and test

```
dotnet build
bin\Debug\net10.0\agentic.exe --selftest        offline checks: editor and screen redraws, tools, permissions, hooks, settings
dotnet publish -o publish                       one self-contained agentic.exe (no .dll or .pdb)
dotnet publish -r linux-x64 -o publish-linux    the same for Linux: one self-contained agentic
```

The Linux build is the same code. The differences are:
- Command lines (hooks, `statusLine`, `$EDITOR`) run through `/bin/sh -c`, and the Bash tool uses bash.
- Without `$EDITOR` the CLI opens nano or vi.
- `/copy` uses wl-copy, xclip or xsel. With none of them it uses the terminal's clipboard (OSC 52).
- Programs are looked up on `PATH` without Windows extensions.
- `agentic --selftest` passes on both platforms.

- The sources are in `src\`.
- The CLI needs an Agentic server with the CLI endpoints (stop, rewind, client tool rounds), from October 2026 or later.

## Troubleshooting

- Symbols such as `⎿`, `✻` or `╭` don't show: your console font lacks them. Use Windows Terminal or VS Code's terminal, where they are detected automatically, or force a choice with `AGENTIC_GLYPHS=simple` (or `unicode`).
- No colours: `NO_COLOR` is set, or use `--no-color`. Editing and redraws work either way.
- Unauthorized: run `agentic login` again with a new API key.

## License

The Agentic CLI is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published by the Free Software Foundation, either version 3 of the License, or (at your option) any later version. It comes WITHOUT ANY WARRANTY. See [LICENSE](LICENSE) for the full text.
