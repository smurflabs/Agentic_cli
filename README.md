<div align="center">

<img src="docs/logo.png" alt="Agentic" width="288">

**Agentic CLI: a coding agent in your terminal, powered by your Agentic server.**

[![Release](https://github.com/smurflabs/Agentic_cli/actions/workflows/release.yml/badge.svg)](https://github.com/smurflabs/Agentic_cli/releases)
[![License: GPL v3](https://img.shields.io/badge/license-GPL--3.0--or--later-blue.svg)](LICENSE)
![.NET 10](https://img.shields.io/badge/.NET-10-512BD4)
![Runs on Windows and Linux](https://img.shields.io/badge/runs%20on-Windows%20%7C%20Linux-lightgrey)

[Install](#install) · [Features](#features) · [Setup guide](SETUP.md) · [Reference manual](docs/REFERENCE.md) · [Agentic server](https://github.com/smurflabs/Agentic)

</div>

---

## What is it?

The Agentic CLI is an AI coding agent that works in your project folder. It reads and edits code, runs commands and tests, and plans and carries out multi-step work, asking before it changes anything you haven't allowed.

The tools run on your computer. The models run on your [Agentic server](https://github.com/smurflabs/Agentic), so the CLI can use:
- every model your account may use, including custom routing models
- the server's web search, code interpreter and MCP tools
- your account's permissions, limits and budget

Conversations are stored on the server, so you can follow and continue them in the web app.

```text
agentic                          start a session in this folder
agentic "fix the failing tests"  start with a first message
agentic -c                       continue the last conversation in this folder
agentic -p "explain src/app.vb"  answer once and exit (scripts, pipes, CI)
```

## Features

### A full coding agent

- **Tools on your machine:**
  - read, write and edit files, keeping their encoding and line endings and refusing edits to files that changed since they were read
  - search with glob and grep
  - run shell commands, including long-running ones in the background
  - fetch web pages
  - keep a task list
  - watch logs and builds with `Monitor`
- **Subagents** that work in parallel on independent parts of a task (`/parallel`).
- **Safe by default:**
  - permission modes (normal, accept edits, plan)
  - allow, ask and deny rules
  - checkpoints with `/rewind`, which restores files, the conversation or both
  - git worktrees (`/worktree`) to keep experiments out of your checkout
- **Built-in workflows:** `/review`, `/security-review`, `/pr-comments`, `/simplify`, `/verify` and `/init`.
- **Run and test the project** with `/run`. It finds how the project starts (npm scripts, .NET, Python, Go, Rust, static sites and more) and shows a dev server's address when it is up. `/run share` publishes a site to a public link.

### Works with your Claude Code setup

- **Your existing Claude Code files work here:** memory files (`AGENTS.md`, `CLAUDE.md`), slash commands, skills, subagents, hooks, MCP servers and plugins.
- **MCP:** connect stdio and HTTP MCP servers, with OAuth sign-in for hosted ones. `agentic mcp serve` offers the CLI's own tools to other apps.

### Everything on your server

- **Models:** every model and custom model your account may use, with a choice of reasoning effort.
- **Research and code:** web search, deep research with sources (`/research`), and the server's code interpreter.
- **Your account across apps:** memories, saved prompts, usage (`/usage`) and feedback to the administrators, shared with the web and desktop apps.
- **Scheduled prompts** (`/schedule`) that run on the server, even while the CLI is closed.
- **Voice:** answers read aloud and hands-free dictation, with the server's voices.
- **VS Code link** (`/ide`): your selection goes with each message, edits show as diffs in VS Code, and the CLI reads VS Code's problems.

### A terminal UI built for long sessions

- **Display:** streaming Markdown with syntax highlighting, coloured diffs and live tool cards.
- **Keys and input:** every key can be rebound (`/keys`), plus vim mode, history search, image paste and editing in your own editor.
- **Long-running work:** loops (`/loop`), a heartbeat checklist, a custom status line and output styles.

### Made for automation

- **Print mode** (`-p`) with text, JSON or streaming JSON output for scripts and CI.
- **`--input-format stream-json`** to drive the CLI from your own programs over stdin and stdout.
- **Self-updating:** the CLI updates itself from your Agentic server.

## Install

### From a release

Download the build for your system from [Releases](https://github.com/smurflabs/Agentic_cli/releases):

| File | System |
|---|---|
| `agentic-cli-<version>-win-x64.zip` | Windows x64: `agentic.exe` |
| `agentic-cli-<version>-linux-x64.tar.gz` | Linux x64: `agentic` |

Extract it into a folder of its own that only you can read, then add that folder to your `PATH`.

### From your Agentic server

When your server's administrator has published the CLI:

```powershell
irm https://your-server/install.ps1 | iex          # Windows
```

```bash
curl -fsSL https://your-server/install.sh | bash   # Linux and macOS
```

### From source

```powershell
dotnet publish -o publish                       # Windows: publish\agentic.exe
dotnet publish -r linux-x64 -o publish-linux    # Linux:   publish-linux/agentic
```

This needs the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

## Sign in

```text
agentic login     choose your server's address, then paste your API key
agentic doctor    check the connection, account, models, tools and settings
```

Create the API key in the Agentic web app: open the user menu, then **API key** → **Generate**. For CI, set `AGENTIC_SERVER` and `AGENTIC_API_KEY` instead.

## Documentation

- **[SETUP.md](SETUP.md):** installing, signing in, updates and troubleshooting.
- **[docs/REFERENCE.md](docs/REFERENCE.md):** the complete manual. It covers every key and command, the tools, permissions, settings, memory files, skills, hooks, MCP servers, print mode and the stream-json protocol.

## Building and testing

```powershell
dotnet build
bin\Debug\net10.0\agentic.exe --selftest    # offline checks: editor, redraws, tools, permissions, hooks, settings
```

The CLI is written in VB.NET on .NET 10. It publishes as one self-contained file for Windows and Linux.

## License

The Agentic CLI is free software: you can redistribute it and/or modify it under the terms of the [GNU General Public License](LICENSE) as published by the Free Software Foundation, either version 3 of the License, or (at your option) any later version.
