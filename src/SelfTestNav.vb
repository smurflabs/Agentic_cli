Option Strict On
Option Explicit On
Option Infer On

Imports System.IO
Imports System.Net
Imports System.Net.Sockets
Imports System.Text
Imports System.Text.Json
Imports AgenticCli.Agent
Imports AgenticCli.Core

''' <summary>
''' Self-tests of Claude Code plugins (/plugin: a folder marketplace, installing, what a plugin brings, on and off,
''' uninstalling) and of the IDE link (/ide: VS Code found through its lock file; its state, problems and diffs).
''' </summary>
Partial Friend NotInheritable Class SelfTest

    Private Shared Sub WriteText(file As String, text As String)
        Directory.CreateDirectory(Path.GetDirectoryName(file))
        IO.File.WriteAllText(file, text)
    End Sub

    Private Shared Async Function NavTests(tmp As String) As Task
        ' ------------------------------------------------------------ plugins
        Dim home = Path.Combine(tmp, "pluginhome")
        Dim project = Path.Combine(tmp, "pluginproject")
        Directory.CreateDirectory(project)
        Dim market = Path.Combine(tmp, "market")
        WriteText(Path.Combine(market, ".claude-plugin", "marketplace.json"), "{""name"": ""team-tools"", ""plugins"": [{""name"": ""demo"", ""source"": ""./plugins/demo"", ""description"": ""Demo""}]}")
        Dim demo = Path.Combine(market, "plugins", "demo")
        WriteText(Path.Combine(demo, ".claude-plugin", "plugin.json"), "{""name"": ""demo"", ""version"": ""1.2.0""}")
        WriteText(Path.Combine(demo, "commands", "hello.md"), "---" & vbLf & "description: Say hello" & vbLf & "---" & vbLf & "Say hello to $ARGUMENTS.")
        WriteText(Path.Combine(demo, "skills", "tidy", "SKILL.md"), "---" & vbLf & "name: tidy" & vbLf & "description: Tidy code" & vbLf & "---" & vbLf & "Tidy it.")
        WriteText(Path.Combine(demo, "agents", "reviewer.md"), "---" & vbLf & "name: reviewer" & vbLf & "description: Reviews" & vbLf & "---" & vbLf & "You review code.")
        WriteText(Path.Combine(demo, "hooks", "hooks.json"), "{""hooks"": {""Stop"": [{""hooks"": [{""type"": ""command"", ""command"": ""node ${CLAUDE_PLUGIN_ROOT}/stop.js""}]}]}}")
        WriteText(Path.Combine(demo, ".mcp.json"), "{""mcpServers"": {""db"": {""command"": ""${CLAUDE_PLUGIN_ROOT}/db.exe""}}}")
        ' A Claude Code plugin with a hook: its hook runs only once turned on in Agentic.
        Dim cc = Path.Combine(home, ".claude", "plugins", "cache", "m", "cc", "1.0.0")
        WriteText(Path.Combine(cc, "hooks", "hooks.json"), "{""hooks"": {""Stop"": [{""hooks"": [{""type"": ""command"", ""command"": ""echo cc""}]}]}}")
        WriteText(Path.Combine(home, ".claude", "plugins", "installed_plugins.json"), "{""version"": 2, ""plugins"": {""cc@m"": [{""scope"": ""user"", ""installPath"": " & Jx.Quote(cc) & "}]}}")
        Dim previousHome = Paths.HomeOverride
        Paths.HomeOverride = home
        Try
            Dim name = Await Plugins.AddMarketplaceAsync(market).ConfigureAwait(False)
            Check("plugin: a folder marketplace is added by its name", name = "team-tools" AndAlso Plugins.Marketplaces().Any(Function(m) m.Name = "team-tools" AndAlso m.Plugins.Any(Function(p) p.Name = "demo")))
            Dim id = Await Plugins.InstallAsync("demo@team-tools").ConfigureAwait(False)
            Dim info = Plugins.Find(id, {project})
            Check("plugin: installed into Agentic's folder, on, with its hooks", id = "demo@team-tools" AndAlso info IsNot Nothing AndAlso info.Enabled AndAlso info.HooksOn AndAlso
                  info.Version = "1.2.0" AndAlso info.Dir.StartsWith(Path.Combine(home, ".agentic", "plugins"), StringComparison.OrdinalIgnoreCase), info?.Dir)
            Check("plugin: what it brings is counted", info IsNot Nothing AndAlso info.Counts.Commands = 1 AndAlso info.Counts.Skills = 1 AndAlso info.Counts.Agents = 1 AndAlso info.Counts.Hooks = 1 AndAlso info.Counts.Mcp = 1)
            Dim store As New Skills.SkillStore(project, True)
            Check("plugin: its command is /demo:hello and its skill demo:tidy", store.Commands().Any(Function(c) c.Name = "demo:hello") AndAlso store.Skills().Any(Function(s) s.Name = "demo:tidy"))
            Check("plugin: its agent is demo:reviewer", Subagents.All(project, True).Any(Function(a) a.Name = "demo:reviewer"))
            Dim settings1 = Core.Settings.Load(project)

            Dim stopHooks = settings1.Hooks("Stop")
            Check("plugin: its hook runs with ${CLAUDE_PLUGIN_ROOT} filled in", stopHooks.Any(Function(h) h.Command.Contains(info.Dir) AndAlso h.Scope = "plugin demo"), String.Join(" | ", stopHooks.Select(Function(h) h.Command)))
            Check("plugin: a Claude Code plugin's hook waits until it is turned on here", Not stopHooks.Any(Function(h) h.Command = "echo cc"))
            Plugins.SetEnabled("cc@m", True)
            Dim settings2 = Core.Settings.Load(project)

            Check("plugin: turned on here, the Claude Code plugin's hook runs", settings2.Hooks("Stop").Any(Function(h) h.Command = "echo cc"))
            Check("plugin: its MCP server is found", Mcp.McpConfig.Load(project, Nothing).Any(Function(s) s.Id = "plugin_demo_db" AndAlso s.PluginRoot = info.Dir))
            Plugins.SetEnabled(id, False)
            Dim off As New Skills.SkillStore(project, True)
            Check("plugin: off, nothing of it loads", Not off.Commands().Any(Function(c) c.Name = "demo:hello") AndAlso Not Subagents.All(project, True).Any(Function(a) a.Name = "demo:reviewer") AndAlso
                  Not Mcp.McpConfig.Load(project, Nothing).Any(Function(s) s.Id = "plugin_demo_db"))
            Plugins.Uninstall(id)
            Check("plugin: uninstalled, its folder and entry are gone", Plugins.Find(id, {project}) Is Nothing AndAlso Not Directory.Exists(info.Dir))
            Plugins.RemoveMarketplace("team-tools")
            Check("plugin: a folder marketplace is removed from the list but kept on disk", Not Plugins.Marketplaces().Any(Function(m) m.Name = "team-tools") AndAlso Directory.Exists(market))
            Check("plugin: owner/repo is GitHub", Plugins.GitUrlOf("anthropics/claude-code") = "https://github.com/anthropics/claude-code.git" AndAlso Plugins.GitUrlOf("https://example.com/m.json") Is Nothing)
        Catch ex As Exception
            Check("plugin: no error", False, ex.ToString())
        Finally
            Paths.HomeOverride = previousHome
        End Try

        ' ------------------------------------------------------------ /ide
        Dim lockDir = Path.Combine(tmp, "ide")
        Environment.SetEnvironmentVariable("AGENTIC_IDE_DIR", lockDir)
        Dim listener = New TcpListener(IPAddress.Loopback, 0)
        listener.Start()
        Dim port = DirectCast(listener.LocalEndpoint, IPEndPoint).Port
        listener.Stop()
        Dim http As New HttpListener()
        http.Prefixes.Add($"http://127.0.0.1:{port}/")
        http.Start()
        Dim lastDiffBody As String = Nothing
        Dim serve = Task.Run(Async Function()
                                 While http.IsListening
                                     Dim c As HttpListenerContext
                                     Try
                                         c = Await http.GetContextAsync().ConfigureAwait(False)
                                     Catch ex As Exception When TypeOf ex Is HttpListenerException OrElse TypeOf ex Is ObjectDisposedException OrElse TypeOf ex Is InvalidOperationException
                                         Exit While
                                     End Try
                                     Dim body As String
                                     Using r As New StreamReader(c.Request.InputStream)
                                         body = r.ReadToEnd()
                                     End Using
                                     Dim answer As String
                                     If c.Request.Headers("Authorization") <> "Bearer tok123" Then
                                         c.Response.StatusCode = 401
                                         answer = "{}"
                                     ElseIf c.Request.Url.AbsolutePath = "/state" Then
                                         answer = "{""activeFile"": " & Jx.Quote(Path.Combine(project, "a.txt")) & ", ""selection"": {""path"": " & Jx.Quote(Path.Combine(project, "a.txt")) & ", ""startLine"": 2, ""endLine"": 3, ""text"": ""two\nthree""}}"
                                     ElseIf c.Request.Url.AbsolutePath = "/diagnostics" Then
                                         answer = "[{""path"": " & Jx.Quote(Path.Combine(project, "a.txt")) & ", ""line"": 2, ""column"": 1, ""severity"": ""error"", ""message"": ""bad"", ""source"": ""ts""}]"
                                     ElseIf c.Request.Url.AbsolutePath = "/diff" Then
                                         lastDiffBody = body
                                         answer = "{""accepted"": true, ""content"": ""one\nTWO\n""}"
                                     Else
                                         c.Response.StatusCode = 404
                                         answer = "{}"
                                     End If
                                     Dim bytes = Encoding.UTF8.GetBytes(answer)
                                     c.Response.ContentType = "application/json"
                                     Await c.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(False)
                                     c.Response.Close()
                                 End While
                             End Function)
        Try
            WriteText(Path.Combine(lockDir, port & ".lock"), Jx.Stringify(New With {.pid = Environment.ProcessId, .port = port, .token = "tok123", .ideName = "VS Code", .workspaceFolders = {project}}))
            WriteText(Path.Combine(lockDir, "1.lock"), Jx.Stringify(New With {.pid = 999999, .port = 1, .token = "x", .workspaceFolders = {project}}))
            Dim found = IdeLink.Available(Path.Combine(project, "src"))
            Check("ide: the editor whose workspace holds this folder is found; a dead one's lock file goes", found.Count = 1 AndAlso found(0).Port = port AndAlso found(0).MatchedFolder = project AndAlso
                  Not IO.File.Exists(Path.Combine(lockDir, "1.lock")), String.Join(",", found.Select(Function(f) f.Port)))
            Dim link As New IdeLink(found(0))
            Dim note = Await link.ContextAsync(Function(p) Path.GetFileName(p), Threading.CancellationToken.None).ConfigureAwait(False)
            Check("ide: the selection goes with the message", note IsNot Nothing AndAlso note.Contains("<ide-selection file=""a.txt"" lines=""2-3"">") AndAlso note.Contains("two" & vbLf & "three"), note)
            Check("ide: the same selection is not sent twice", Await link.ContextAsync(Function(p) Path.GetFileName(p), Threading.CancellationToken.None).ConfigureAwait(False) Is Nothing)
            Dim d = Await link.DiffAsync(Path.Combine(project, "a.txt"), "one" & vbLf & "TWO" & vbLf & "THREE" & vbLf, Threading.CancellationToken.None).ConfigureAwait(False)
            Check("ide: a diff comes back accepted with what the user kept", d.Accepted AndAlso d.Content = "one" & vbLf & "TWO" & vbLf AndAlso lastDiffBody IsNot Nothing AndAlso lastDiffBody.Contains("THREE"))
            Dim st As New AgentState With {.ProjectDir = project, .Cwd = project, .Ide = link}
            Dim r = Await New Tools.IdeDiagnosticsTool().RunAsync(Jx.Parse("{}"), New Tools.ToolContext With {.State = st, .Cancel = Threading.CancellationToken.None}).ConfigureAwait(False)
            Check("ide: IdeDiagnostics lists the editor's problems", r.Ok AndAlso r.Content.Contains(":2:1 error: bad [ts]"), r.Content)
        Catch ex As Exception
            Check("ide: no error", False, ex.ToString())
        Finally
            http.Stop()
            Environment.SetEnvironmentVariable("AGENTIC_IDE_DIR", Nothing)
        End Try
    End Function

End Class
