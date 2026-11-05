Option Strict On
Option Explicit On
Option Infer On

Imports System.IO
Imports System.Net
Imports System.Net.Http
Imports System.Net.Sockets
Imports System.Security.Cryptography
Imports System.Text
Imports AgenticCli.Core
Imports AgenticCli.Mcp

''' <summary>
''' Self-tests of the MCP servers: variables (${VAR}, ${VAR:-default}, ${CLAUDE_PLUGIN_ROOT}), servers found in Claude Code
''' (~\.claude.json, plugins) used as they are, pictures from a stdio server, and the OAuth sign-in of a hosted server.
''' </summary>
Partial Friend NotInheritable Class SelfTest

    ' A tiny stdio MCP server: initialize, tools/list, tools/call with a picture, the env it got, resources, prompts and a
    ' question to the user (elicitation).
    Private Const FakeMcpServer As String =
"const rl = require('readline').createInterface({ input: process.stdin });
const send = m => process.stdout.write(JSON.stringify(m) + '\n');
let pendingAsk = null;
rl.on('line', line => {
  const m = JSON.parse(line);
  if (m.id === 'q1' && pendingAsk !== null) { send({ jsonrpc: '2.0', id: pendingAsk, result: { content: [{ type: 'text', text: 'answer ' + JSON.stringify(m.result) }] } }); pendingAsk = null; return; }
  if (m.method === 'initialize') send({ jsonrpc: '2.0', id: m.id, result: { protocolVersion: '2025-06-18', capabilities: { tools: {}, resources: {}, prompts: {} }, serverInfo: { name: 'fake' } } });
  else if (m.method === 'tools/list') send({ jsonrpc: '2.0', id: m.id, result: { tools: [{ name: 'shot' }, { name: 'env' }, { name: 'ask' }] } });
  else if (m.method === 'resources/list') send({ jsonrpc: '2.0', id: m.id, result: { resources: [{ uri: 'memo://one', name: 'One' }] } });
  else if (m.method === 'resources/read') send({ jsonrpc: '2.0', id: m.id, result: { contents: [{ uri: m.params.uri, text: 'content of ' + m.params.uri }] } });
  else if (m.method === 'prompts/list') send({ jsonrpc: '2.0', id: m.id, result: { prompts: [{ name: 'review', arguments: [{ name: 'file', required: true }, { name: 'focus' }] }] } });
  else if (m.method === 'prompts/get') send({ jsonrpc: '2.0', id: m.id, result: { messages: [{ role: 'user', content: { type: 'text', text: 'Review ' + m.params.arguments.file + ' for ' + (m.params.arguments.focus || 'all') } }] } });
  else if (m.method === 'tools/call' && m.params.name === 'shot') send({ jsonrpc: '2.0', id: m.id, result: { content: [{ type: 'text', text: 'here' }, { type: 'image', mimeType: 'image/png', data: 'iVBORw0KGgo=' }] } });
  else if (m.method === 'tools/call' && m.params.name === 'ask') { pendingAsk = m.id; send({ jsonrpc: '2.0', id: 'q1', method: 'elicitation/create', params: { message: 'Which branch?', requestedSchema: { type: 'object', properties: { branch: { type: 'string' } } } } }); }
  else if (m.method === 'tools/call') send({ jsonrpc: '2.0', id: m.id, result: { content: [{ type: 'text', text: process.env.FAKE_TOKEN + '|' + process.argv.slice(2).join('|') }] } });
});"

    Private Shared Async Function McpTests(tmp As String) As Task
        ' Variables.
        Environment.SetEnvironmentVariable("AGENTIC_SELFTEST_TOKEN", "tok-1")
        Dim missing As New List(Of String)
        Check("mcp: ${VAR} and ${VAR:-default}", McpConfig.Expand("Bearer ${AGENTIC_SELFTEST_TOKEN} ${NOPE_NOT_SET:-dflt}") = "Bearer tok-1 dflt")
        Check("mcp: ${CLAUDE_PLUGIN_ROOT} and ${env:X}", McpConfig.Expand("${CLAUDE_PLUGIN_ROOT}/s.js ${env:AGENTIC_SELFTEST_TOKEN}", pluginRoot:="/p") = "/p/s.js tok-1")
        McpConfig.Expand("${GITHUB_TOKEN_SURELY_UNSET}", missing:=missing)
        Check("mcp: an unset variable is named", missing.SequenceEqual({"GITHUB_TOKEN_SURELY_UNSET"}), String.Join(",", missing))
        If OperatingSystem.IsWindows() Then Check("mcp: %VAR% on Windows", McpConfig.Expand("%AGENTIC_SELFTEST_TOKEN%") = "tok-1")
        Dim vs = McpConfig.Parse("{""servers"": {""ws"": {""type"": ""http"", ""url"": ""https://x/mcp"", ""oauth"": {""clientId"": ""abc""}}}}")
        Check("mcp: VS Code's layout and oauth settings", vs.Count = 1 AndAlso vs(0).Transport = McpTransportKind.Http AndAlso vs(0).OAuthClientId = "abc")

        ' Claude Code's servers, its plugins' and this project's, used live; Agentic's own file wins for the same id.
        Dim home = Path.Combine(tmp, "mcphome")
        Dim project = Path.Combine(tmp, "mcpproject")
        Dim plugin = Path.Combine(home, ".claude", "plugins", "cache", "m", "db", "1.0.0")
        Directory.CreateDirectory(plugin)
        Directory.CreateDirectory(project)
        Directory.CreateDirectory(Path.Combine(home, ".agentic"))
        File.WriteAllText(Path.Combine(plugin, ".mcp.json"), "{""mcpServers"": {""db"": {""command"": ""${CLAUDE_PLUGIN_ROOT}/db.exe""}}}")
        File.WriteAllText(Path.Combine(home, ".claude", "plugins", "installed_plugins.json"),
                          "{""version"": 2, ""plugins"": {""db@m"": [{""scope"": ""user"", ""installPath"": " & Jx.Quote(plugin) & "}]}}")
        File.WriteAllText(Path.Combine(home, ".claude.json"),
                          "{""mcpServers"": {""cc"": {""type"": ""http"", ""url"": ""https://example.com/mcp""}, ""both"": {""command"": ""claude-one""}}, ""projects"": {" &
                          Jx.Quote(project.Replace("\"c, "/"c)) & ": {""mcpServers"": {""local1"": {""command"": ""node""}}}}}")
        Dim previousHome = Paths.HomeOverride
        Paths.HomeOverride = home
        Try
            Dim agenticFile = Path.Combine(home, ".agentic", "mcp.json")
            Dim all = McpConfig.Load(project, Nothing)
            Dim by = Function(id As String) all.FirstOrDefault(Function(s) s.Id = id)
            Check("mcp: Claude Code's user servers", by("cc") IsNot Nothing AndAlso by("cc").Scope = "claude" AndAlso by("cc").Source = "Claude Code")
            Check("mcp: this project's servers in ~/.claude.json", by("local1") IsNot Nothing AndAlso by("local1").Scope = "claude-local")
            Check("mcp: a plugin's server, with its folder", by("plugin_db_db") IsNot Nothing AndAlso by("plugin_db_db").PluginRoot = plugin AndAlso Not by("plugin_db_db").Editable,
                  String.Join(",", all.Select(Function(s) s.Id)))
            Check("mcp: without includeClaudeConfig only Agentic's own", Not McpConfig.Load(project, Nothing, includeClaude:=False).Any(Function(s) s.Id = "cc"))
        Finally
            Paths.HomeOverride = previousHome
        End Try

        ' A stdio server: its picture comes back as a picture, ${VAR} fills its env and arguments.
        If Platform.Which("node") Is Nothing Then
            Check("mcp: node for the stdio test", False, "node is not on PATH")
            Return
        End If
        Dim script = Path.Combine(tmp, "fake-mcp.js")
        File.WriteAllText(script, FakeMcpServer)
        Dim opts As New McpServerOptions With {.Id = "fake", .Name = "fake", .Command = "node", .Args = New List(Of String) From {script, "${AGENTIC_SELFTEST_TOKEN}", "two words"}}
        opts.Env("FAKE_TOKEN") = "${AGENTIC_SELFTEST_TOKEN:-none}"
        Using c As New McpConnection(opts)
            Await c.StartAsync().ConfigureAwait(False)
            Check("mcp: stdio connects", c.Status.Connected, c.Status.Error)
            Dim shot = Await c.CallToolAsync("shot", "{}").ConfigureAwait(False)
            Check("mcp: a picture comes back as a picture", shot.Ok AndAlso shot.Images.Count = 1 AndAlso shot.Images(0).MediaType = "image/png" AndAlso shot.Content.Contains("picture 1"), shot.Content)
            Dim env = Await c.CallToolAsync("env", "{}").ConfigureAwait(False)
            Check("mcp: ${VAR} in env and arguments", env.Content = "tok-1|tok-1|two words", env.Content)
            ' Resources, prompts, and a question from the server.
            Check("mcp: resources and prompts are offered", c.HasCapability("resources") AndAlso c.Prompts.Count = 1 AndAlso c.Prompts(0).Args.Count = 2 AndAlso c.Prompts(0).Args(0).Required)
            Dim res = Await c.ReadResourceAsync("memo://one").ConfigureAwait(False)
            Check("mcp: a resource is read", res.Ok AndAlso res.Content = "content of memo://one", If(res.Content, res.Error))
            Dim listed = Await c.ListAllAsync("resources/list", "resources").ConfigureAwait(False)
            Check("mcp: resources are listed", listed.Count = 1 AndAlso Jx.StrOf(listed(0), "uri") = "memo://one")
            Dim prompt = Await c.GetPromptAsync("review", New Dictionary(Of String, String) From {{"file", "a.vb"}, {"focus", "bugs"}}).ConfigureAwait(False)
            Check("mcp: a prompt's text", prompt = "Review a.vb for bugs", prompt)
            Dim previousElicit = McpConnection.Elicit
            McpConnection.Elicit = Function(server, message, schema) Task.FromResult(New Text.Json.Nodes.JsonObject From {{"action", "accept"}, {"content", New Text.Json.Nodes.JsonObject From {{"branch", "main"}}}})
            Dim asked = Await c.CallToolAsync("ask", "{}").ConfigureAwait(False)
            McpConnection.Elicit = previousElicit
            Check("mcp: a question from the server is answered", asked.Content = "answer {""action"":""accept"",""content"":{""branch"":""main""}}", asked.Content)
        End Using

        ' agentic mcp serve: this CLI's tools for other MCP clients (here, its own client).
        Dim serveDir = Path.Combine(tmp, "serve")
        Directory.CreateDirectory(serveDir)
        File.WriteAllText(Path.Combine(serveDir, "note.txt"), "served text" & vbLf)
        Dim serveOpts As New McpServerOptions With {.Id = "self", .Name = "self", .Command = Environment.ProcessPath, .Args = New List(Of String) From {"mcp", "serve"}, .Cwd = serveDir}
        Using c As New McpConnection(serveOpts)
            Await c.StartAsync().ConfigureAwait(False)
            Check("mcp serve: another app connects to this CLI's tools", c.Status.Connected AndAlso c.Status.Tools.Any(Function(t) t.Name = "Read") AndAlso c.Status.Tools.Any(Function(t) t.Name = "Bash"),
                  If(c.Status.Error, String.Join(",", c.Status.Tools.Select(Function(t) t.Name))))
            Dim read = Await c.CallToolAsync("Read", Jx.Stringify(New With {.file_path = Path.Combine(serveDir, "note.txt")})).ConfigureAwait(False)
            Check("mcp serve: Read works through it", read.Ok AndAlso read.Content.Contains("served text"), If(read.Content, read.Error))
        End Using

        Await OAuthTestAsync(tmp).ConfigureAwait(False)
    End Function

    ''' <summary>A hosted server that answers 401: discovery, registration, PKCE through a "browser", then the tools work; a refused token is refreshed.</summary>
    Private Shared Async Function OAuthTestAsync(tmp As String) As Task
        Dim port = FreeTcpPort()
        Dim base = $"http://localhost:{port}"
        Dim listener As New HttpListener()
        listener.Prefixes.Add(base & "/")
        Try
            listener.Start()
        Catch ex As HttpListenerException
            Check("mcp oauth: fake server starts", False, ex.Message)
            Return
        End Try
        Dim codes As New Concurrent.ConcurrentDictionary(Of String, String)()
        Dim tokens As New Concurrent.ConcurrentDictionary(Of String, Boolean)()
        Dim refreshed = 0
        Dim serving = Task.Run(Async Function()
                                   While listener.IsListening
                                       Dim ctx As HttpListenerContext
                                       Try
                                           ctx = Await listener.GetContextAsync().ConfigureAwait(False)
                                       Catch ex As Exception When TypeOf ex Is HttpListenerException OrElse TypeOf ex Is ObjectDisposedException OrElse TypeOf ex Is InvalidOperationException
                                           Exit While
                                       End Try
                                       Dim req = ctx.Request, res = ctx.Response
                                       Dim body As String
                                       Using r As New StreamReader(req.InputStream)
                                           body = Await r.ReadToEndAsync().ConfigureAwait(False)
                                       End Using
                                       Dim send = Sub(status As Integer, json As String)
                                                      res.StatusCode = status
                                                      res.ContentType = "application/json"
                                                      Dim b = Encoding.UTF8.GetBytes(json)
                                                      res.OutputStream.Write(b, 0, b.Length)
                                                      res.Close()
                                                  End Sub
                                       Dim p = req.Url.AbsolutePath
                                       If p = "/.well-known/oauth-protected-resource/mcp" Then
                                           send(200, "{""resource"": """ & base & "/mcp"", ""authorization_servers"": [""" & base & """]}")
                                       ElseIf p = "/.well-known/oauth-authorization-server" Then
                                           send(200, "{""issuer"": """ & base & """, ""authorization_endpoint"": """ & base & "/authorize"", ""token_endpoint"": """ & base & "/token"", ""registration_endpoint"": """ & base & "/register""}")
                                       ElseIf p = "/register" Then
                                           send(201, "{""client_id"": ""client-1""}")
                                       ElseIf p = "/authorize" Then
                                           Dim code = "code-" & Guid.NewGuid().ToString("N").Substring(0, 6)
                                           codes(code) = req.QueryString("code_challenge")
                                           res.StatusCode = 302
                                           res.RedirectLocation = req.QueryString("redirect_uri") & "?code=" & code & "&state=" & req.QueryString("state")
                                           res.Close()
                                       ElseIf p = "/token" Then
                                           Dim f = Web.HttpUtility.ParseQueryString(body)
                                           If f("grant_type") = "refresh_token" Then
                                               refreshed += 1
                                               tokens("tok-r" & refreshed) = True
                                               send(200, "{""access_token"": ""tok-r" & refreshed & """, ""expires_in"": 3600}")
                                           Else
                                               Dim challenge As String = Nothing
                                               Dim verifier = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(If(f("code_verifier"), "")))).Replace("+"c, "-"c).Replace("/"c, "_"c).TrimEnd("="c)
                                               If codes.TryGetValue(If(f("code"), ""), challenge) AndAlso challenge = verifier Then
                                                   tokens("tok-1") = True
                                                   send(200, "{""access_token"": ""tok-1"", ""refresh_token"": ""refresh-1"", ""expires_in"": 3600}")
                                               Else
                                                   send(400, "{""error"": ""invalid_grant""}")
                                               End If
                                           End If
                                       ElseIf p = "/mcp" Then
                                           Dim auth = If(req.Headers("Authorization"), "").Replace("Bearer ", "")
                                           If Not tokens.ContainsKey(auth) Then
                                               res.StatusCode = 401
                                               res.AddHeader("WWW-Authenticate", $"Bearer resource_metadata=""{base}/.well-known/oauth-protected-resource/mcp""")
                                               res.Close()
                                           ElseIf body.Contains("""method"":""initialize""") Then
                                               send(200, "{""jsonrpc"":""2.0"",""id"":" & RegularExpressions.Regex.Match(body, """id"":(\d+)").Groups(1).Value & ",""result"":{""protocolVersion"":""2025-06-18"",""capabilities"":{},""serverInfo"":{""name"":""hosted""}}}")
                                           ElseIf body.Contains("""method"":""tools/list""") Then
                                               send(200, "{""jsonrpc"":""2.0"",""id"":" & RegularExpressions.Regex.Match(body, """id"":(\d+)").Groups(1).Value & ",""result"":{""tools"":[{""name"":""whoami""}]}}")
                                           ElseIf body.Contains("""method"":""tools/call""") Then
                                               send(200, "{""jsonrpc"":""2.0"",""id"":" & RegularExpressions.Regex.Match(body, """id"":(\d+)").Groups(1).Value & ",""result"":{""content"":[{""type"":""text"",""text"":""you are " & auth & """}]}}")
                                           Else
                                               res.StatusCode = 202
                                               res.Close()
                                           End If
                                       Else
                                           send(404, "{}")
                                       End If
                                   End While
                               End Function)
        Dim previousStore = McpOAuth.StoreFile
        Dim previousBrowser = McpOAuth.OpenBrowser
        McpOAuth.StoreFile = Path.Combine(tmp, "mcp-auth-test.json")
        Dim opened As String = Nothing
        McpOAuth.OpenBrowser = Async Function(u As String) As Task
                                   opened = u
                                   Using h As New HttpClient()
                                       Await h.GetAsync(u).ConfigureAwait(False)
                                   End Using
                               End Function
        Try
            Dim opts As New McpServerOptions With {.Id = "hosted", .Name = "hosted", .Transport = McpTransportKind.Http, .Endpoint = base & "/mcp"}
            Using c As New McpConnection(opts)
                Await c.StartAsync().ConfigureAwait(False)
                Check("mcp oauth: a 401 asks for a sign-in", Not c.Status.Connected AndAlso c.Status.NeedsSignIn, c.Status.Error)
                Await McpOAuth.SignInAsync(opts.Endpoint, c.WwwAuthenticate).ConfigureAwait(False)
                Check("mcp oauth: the browser got a PKCE request", opened IsNot Nothing AndAlso opened.Contains("code_challenge_method=S256") AndAlso opened.Contains("client_id=client-1"), opened)
            End Using
            Using c As New McpConnection(opts)
                Await c.StartAsync().ConfigureAwait(False)
                Check("mcp oauth: signed in, it connects", c.Status.Connected, c.Status.Error)
                Dim r = Await c.CallToolAsync("whoami", "{}").ConfigureAwait(False)
                Check("mcp oauth: the token goes with each call", r.Content = "you are tok-1", r.Content)
                Dim stale As Boolean
                tokens.TryRemove("tok-1", stale)
                r = Await c.CallToolAsync("whoami", "{}").ConfigureAwait(False)
                Check("mcp oauth: a refused token is refreshed once", r.Content = "you are tok-r1", If(r.Content, r.Error))
            End Using
            McpOAuth.SignOut(opts.Endpoint)
            Check("mcp oauth: signing out forgets the tokens", Not McpOAuth.SignedIn(opts.Endpoint))
        Finally
            McpOAuth.StoreFile = previousStore
            McpOAuth.OpenBrowser = previousBrowser
            listener.Stop()
            listener.Close()
        End Try
    End Function

End Class
