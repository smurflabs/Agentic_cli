# Setting up the Agentic CLI

The CLI is a coding agent in your terminal. The models run on your Agentic server (the **Agentic** repository). The tools run on your computer: files, search, shell commands, web pages, subagents, skills and MCP servers. [README.md](README.md) covers sessions, commands, permissions, hooks and MCP in depth.

## 1. What you need

- **A running Agentic server.** See the Agentic repository's `SETUP.md`.
- **An account on that server with the Chat & model API permission.** An administrator grants it under **Users & access**.
- **A personal API key.** In the web app, open the user menu → **API key** → **Generate**.
- **To build from source:** the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). The published `agentic.exe` is self-contained, so the machines that run it need no .NET.

Windows and Linux (x64) are supported. macOS builds (`-r osx-arm64` or `-r osx-x64`) are made the same way but are less tested.

## 2. Install

### From your server (no build)

When the server's administrator has published the CLI (Agentic `SETUP.md`, section 9):

```powershell
irm https://your-server/install.ps1 | iex          # Windows: installs into %LOCALAPPDATA%\Programs\Agentic and adds it to PATH
```

```bash
curl -fsSL https://your-server/install.sh | bash   # Linux, macOS: installs into ~/.local/share/agentic, links ~/.local/bin/agentic
```

The installer already points the CLI at that server. You can also download it from **Apps & updates** in the web app.

### From source

```powershell
git clone <this repository> Agentic_cli
cd Agentic_cli
dotnet build
bin\Debug\net10.0\agentic.exe --selftest       # offline checks: editor, tools, permissions, hooks, settings
dotnet publish -o publish                      # Windows: one self-contained publish\agentic.exe
dotnet publish -r linux-x64 -o publish-linux   # Linux: one self-contained publish-linux/agentic
```

Then:
1. Copy the published file into a folder of its own that only you can read. `agentic login` stores the API key next to the file, and the CLI replaces this file when it updates itself.
2. Put that folder on your `PATH`. On Linux you can link the file instead: `ln -s "$PWD/agentic" ~/.local/bin/agentic`.

A development build (`bin\Debug`, with `agentic.dll` next to it) never updates itself.

## 3. Sign in

```
agentic login
```

1. **Server:** the CLI offers a default address. Pick **Another address…** and type your server's address, for example `https://agentic.example.com` or `http://127.0.0.1:8080`.
2. **API key:** paste the key from step 1.

Both are saved in `config.json` next to `agentic.exe`:

```json
{ "serverBaseUrl": "https://agentic.example.com", "apiKey": "agk_..." }
```

The key is stored there as plain text. For CI and scripts, set `AGENTIC_SERVER` and `AGENTIC_API_KEY` instead; they override `config.json`. `agentic logout` removes the key and keeps the address.

Check everything with:

```
agentic doctor
```

It tests the connection, your account, the models, the tools on this computer and your settings.

## 4. First session

```
cd path\to\your\project
agentic                           start a session in this folder
agentic "fix the failing tests"   start with a first message
agentic -c                        continue the last conversation in this folder
agentic -p "explain src/app.vb"   answer once and exit (scripts, pipes, CI)
```

Type `/help` in a session to list the commands. The CLI asks before it edits files or runs commands. [README.md](README.md#permissions) explains how to change that.

## 5. Where things are

| Path | What |
|---|---|
| `config.json` next to `agentic.exe` | Server address and API key |
| `~\.agentic\` | `settings.json`, `mcp.json`, `AGENTS.md`, commands, skills, agents |
| `~\.agentic\projects\<project>\` | Conversations started in the project, input history, checkpoints for `/rewind` |

Set `AGENTIC_HOME` to keep `~\.agentic` somewhere else. Conversations are also stored on the server and show in the web app's Chat tab.

## 6. Updates

An installed CLI updates itself from your server when an interactive session starts. `agentic update` installs a newer build now. To turn this off, set `"autoUpdates": false` in settings or `DISABLE_AUTOUPDATER=1`.

To release a new version from source:
1. Raise `<Version>` in `AgenticCli.vbproj`.
2. Run `.\tools\publish-clients.ps1 -Apps cli` in the Agentic repository, with this repository cloned next to it.

## 7. Troubleshooting

| Symptom | Fix |
|---|---|
| `Not connected: run "agentic login" first` | Run `agentic login`, or set `AGENTIC_SERVER` and `AGENTIC_API_KEY`. |
| 401 / Unauthorized | The key was revoked or mistyped: generate a new one and run `agentic login`. |
| 403 on Chat | Your account lacks **Chat & model API** (or the chosen model is not allowed for you). |
| Symbols such as `⎿` or `╭` don't show | Use Windows Terminal or VS Code's terminal, or set `AGENTIC_GLYPHS=simple`. |
| No colours | `NO_COLOR` is set or `--no-color` was given. |

## License

The Agentic CLI is free software, licensed under the GNU General Public License v3.0 or later. See [LICENSE](LICENSE).
