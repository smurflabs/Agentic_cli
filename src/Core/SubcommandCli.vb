Option Strict On
Option Explicit On
Option Infer On

Imports System.IO
Imports System.Text.Json
Imports System.Text.Json.Nodes
Imports AgenticCli.Mcp
Imports AgenticCli.Ui

Namespace Core

    ''' <summary>agentic mcp …: the same commands as "claude mcp".</summary>
    Friend NotInheritable Class McpCli

        Private Sub New()
        End Sub

        Private Const Help As String = "agentic mcp list                                 Servers and where they are configured
agentic mcp get <name>                           One server's settings
agentic mcp add [options] <name> -- <command> [args...]
agentic mcp add --transport http|sse <name> <url> [-H ""Header: value""]
    options: -s, --scope user|project   -e, --env KEY=value   --timeout <seconds>   --approval (ask before each tool)
agentic mcp add-json <name> '<json>' [-s user|project]   (Claude Code's server JSON)
agentic mcp remove <name> [-s user|project]
agentic mcp enable|disable <name>
agentic mcp test <name>                          Connect and list its tools
agentic mcp login|logout <name>                  Sign in to a hosted (HTTP) server with OAuth in the browser, or forget it
agentic mcp serve                                This CLI's tools (Read, Edit, Bash …) as an MCP server over stdio, for other apps
agentic mcp import [--all]                       Copy servers from Claude Code, Claude Desktop or the Agentic desktop app
agentic mcp catalog                              Well-known servers you can add by name
agentic mcp add <catalog name> [folder] [-e KEY=value]   e.g. agentic mcp add playwright, agentic mcp add filesystem C:work"

        Public Shared Async Function RunAsync(args As List(Of String)) As Task(Of Integer)
            Dim cwd = Environment.CurrentDirectory
            Dim verb = If(args.Count > 0, args(0).ToLowerInvariant(), "list")
            Dim rest = args.Skip(1).ToList()
            Select Case verb
                Case "list", "ls"
                    Dim all = McpConfig.Load(cwd, Nothing)
                    If all.Count = 0 Then
                        Console.WriteLine("No MCP servers. Add one with: agentic mcp add <name> -- <command> [args]")
                        Return 0
                    End If
                    For Each s In all
                        Console.WriteLine($"{s.Id,-22} {If(s.Source, s.Scope),-28} {s.TransportName(),-6} {If(s.Enabled, "", "(disabled) ")}{If(s.Transport <> McpTransportKind.Stdio AndAlso McpOAuth.SignedIn(McpConfig.Expand(s.Endpoint, cwd, s.PluginRoot)), "(signed in) ", "")}{If(s.Transport = McpTransportKind.Stdio, s.Command & " " & String.Join(" ", s.Args), s.Endpoint)}")
                    Next
                    Return 0
                Case "get"
                    Dim s = McpConfig.Load(cwd, Nothing).FirstOrDefault(Function(x) x.Id.Equals(If(rest.FirstOrDefault(), ""), StringComparison.OrdinalIgnoreCase))
                    If s Is Nothing Then Return Fail("No such server.")
                    Console.WriteLine(Jx.Stringify(s.ToConfig(), True))
                    Return 0
                Case "add" : Return Add(rest, cwd)
                Case "serve" : Return Await McpServe.RunAsync().ConfigureAwait(False)
                Case "login", "logout"
                    Dim s = McpConfig.Load(cwd, Nothing).FirstOrDefault(Function(x) x.Id.Equals(If(rest.FirstOrDefault(), ""), StringComparison.OrdinalIgnoreCase))
                    If s Is Nothing OrElse s.Transport = McpTransportKind.Stdio Then Return Fail("Give an HTTP server: agentic mcp " & verb & " <name>")
                    Dim url = McpConfig.Expand(s.Endpoint, cwd, s.PluginRoot)
                    If verb = "logout" Then
                        McpOAuth.SignOut(url)
                        Console.WriteLine("Signed out of " & s.Name)
                        Return 0
                    End If
                    Console.WriteLine("Opening the browser to sign in to " & s.Name & "… (waiting up to 5 minutes)")
                    Try
                        Await McpOAuth.SignInAsync(url, Nothing, s.OAuthClientId, s.OAuthClientSecret, s.OAuthScope, s.OAuthCallbackPort).ConfigureAwait(False)
                    Catch ex As Exception When TypeOf ex Is InvalidOperationException OrElse TypeOf ex Is TimeoutException OrElse TypeOf ex Is Net.Http.HttpRequestException OrElse TypeOf ex Is Net.Sockets.SocketException
                        Return Fail("Sign-in failed: " & ex.Message)
                    End Try
                    Console.WriteLine("Signed in to " & s.Name)
                    Return 0
                Case "catalog", "browse"
                    Dim installed = McpConfig.Load(cwd, Nothing).Select(Function(s) s.Id).ToHashSet(StringComparer.OrdinalIgnoreCase)
                    For Each l In McpCatalog.Describe(installed)
                        Console.WriteLine(l)
                    Next
                    Console.WriteLine()
                    Console.WriteLine("Add one: agentic mcp add <name> [folder] [-e KEY=value]   (keys you leave out are read from your environment)")
                    Return 0
                Case "add-json"
                    If rest.Count < 2 Then Return Fail("Usage: agentic mcp add-json <name> '<json>'")
                    Dim scope = ScopeOf(rest)
                    Dim parsed = McpConfig.Parse("{""mcpServers"":{" & Jx.Quote(rest(0)) & ":" & rest(1) & "}}")
                    If parsed.Count = 0 Then Return Fail("That JSON is not a server definition (needs command or url).")
                    McpConfig.Upsert(parsed(0), scope, cwd)
                    Console.WriteLine($"Added {parsed(0).Id} ({scope}).")
                    Return 0
                Case "remove", "rm"
                    If rest.Count = 0 Then Return Fail("Usage: agentic mcp remove <name>")
                    Dim scope = ScopeOf(rest)
                    If McpConfig.Remove(rest(0), scope, cwd) OrElse McpConfig.Remove(rest(0), If(scope = "user", "project", "user"), cwd) Then
                        Console.WriteLine("Removed " & rest(0))
                        Return 0
                    End If
                    Return Fail("No such server.")
                Case "enable", "disable"
                    If rest.Count = 0 Then Return Fail("Usage: agentic mcp " & verb & " <name>")
                    Dim s = McpConfig.Load(cwd, Nothing).FirstOrDefault(Function(x) x.Id.Equals(rest(0), StringComparison.OrdinalIgnoreCase))
                    If s Is Nothing Then Return Fail("No such server.")
                    McpConfig.SetEnabled(s.Id, verb = "enable", s.Scope, cwd)
                    Console.WriteLine($"{s.Id} {verb}d.")
                    Return 0
                Case "test"
                    Dim s = McpConfig.Load(cwd, Nothing).FirstOrDefault(Function(x) x.Id.Equals(If(rest.FirstOrDefault(), ""), StringComparison.OrdinalIgnoreCase))
                    If s Is Nothing Then Return Fail("No such server.")
                    Using c As New McpConnection(s) With {.Roots = New List(Of String) From {cwd}}
                        Await c.StartAsync().ConfigureAwait(False)
                        If Not c.Status.Connected Then Return Fail("Could not connect: " & c.Status.Error)
                        Console.WriteLine($"Connected to {s.Id} ({c.Status.ServerInfo}): {c.Status.Tools.Count} tools")
                        For Each t In c.Status.Tools
                            Console.WriteLine("  " & t.Name & " — " & Tools.ToolKit.OneLine(t.Description, 100))
                        Next
                    End Using
                    Return 0
                Case "import"
                    Dim found = McpConfig.FindClaudeServers()
                    If found.Count = 0 Then
                        Console.WriteLine("No servers found in Claude Code, Claude Desktop or the Agentic desktop app.")
                        Return 0
                    End If
                    Dim existing = McpConfig.Load(cwd, Nothing).Select(Function(s) s.Id).ToHashSet(StringComparer.OrdinalIgnoreCase)
                    Dim imported = 0
                    For Each f In found
                        If existing.Contains(f.Server.Id) Then Continue For
                        Dim take = rest.Contains("--all") OrElse Not Ansi.Interactive OrElse
                                   Menu.Confirm($" Import {Ansi.Bold(f.Server.Id)} from {f.Source}? " & Ansi.Muted(If(f.Server.Transport = McpTransportKind.Stdio, f.Server.Command, f.Server.Endpoint)), True)
                        If Not take Then Continue For
                        McpConfig.Upsert(f.Server, "user", cwd)
                        existing.Add(f.Server.Id)
                        imported += 1
                    Next
                    Console.WriteLine($"Imported {imported} server(s) into {Paths.UserMcpFile()}.")
                    Return 0
                Case Else
                    Console.WriteLine(Help)
                    Return If(verb = "help", 0, 2)
            End Select
        End Function

        Private Shared Function ScopeOf(args As List(Of String)) As String
            For i = 0 To args.Count - 2
                If args(i) = "-s" OrElse args(i) = "--scope" Then
                    Dim v = args(i + 1).ToLowerInvariant()
                    args.RemoveRange(i, 2)
                    Return If(v = "project", "project", "user")
                End If
            Next
            Return "user"
        End Function

        Private Shared Function Add(args As List(Of String), cwd As String) As Integer
            Dim dash = args.IndexOf("--")
            Dim head = If(dash >= 0, args.Take(dash).ToList(), args.ToList())
            Dim tail = If(dash >= 0, args.Skip(dash + 1).ToList(), New List(Of String))
            Dim scope = "user"
            Dim transport = "stdio"
            Dim env As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
            Dim headers As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
            Dim timeout = 60
            Dim approval = False
            Dim positional As New List(Of String)
            Dim i = 0
            While i < head.Count
                Dim a = head(i)
                Select Case a
                    Case "-s", "--scope" : i += 1 : scope = If(head(i).ToLowerInvariant() = "project", "project", "user")
                    Case "-t", "--transport" : i += 1 : transport = head(i).ToLowerInvariant()
                    Case "-e", "--env"
                        i += 1
                        Dim kv = head(i).Split("="c, 2)
                        If kv.Length = 2 Then env(kv(0)) = kv(1)
                    Case "-H", "--header"
                        i += 1
                        Dim kv = head(i).Split(":"c, 2)
                        If kv.Length = 2 Then headers(kv(0).Trim()) = kv(1).Trim()
                    Case "--timeout" : i += 1 : Integer.TryParse(head(i), timeout)
                    Case "--approval" : approval = True
                    Case Else : positional.Add(a)
                End Select
                i += 1
            End While
            If positional.Count = 0 Then Return Fail("Usage: agentic mcp add <name> -- <command> [args]   or   agentic mcp add --transport http <name> <url>")
            ' A well-known server by its catalog name: "agentic mcp add playwright", "agentic mcp add filesystem C:work".
            Dim entry = If(tail.Count = 0 AndAlso transport = "stdio", McpCatalog.Find(positional(0)), Nothing)
            If entry IsNot Nothing AndAlso (positional.Count = 1 OrElse (entry.NeedsFolder AndAlso positional.Count = 2)) Then
                Dim folder = If(positional.Count > 1, positional(1), cwd)
                Dim fromCatalog = McpCatalog.ToServer(entry, Path.GetFullPath(folder), env)
                fromCatalog.TimeoutSeconds = timeout
                fromCatalog.RequireApproval = fromCatalog.RequireApproval OrElse approval
                McpConfig.Upsert(fromCatalog, scope, cwd)
                Console.WriteLine($"Added {entry.Name} as {fromCatalog.Id} ({fromCatalog.TransportName()}, {scope}).")
                If entry.NeedsFolder Then Console.WriteLine("Folder: " & Path.GetFullPath(folder))
                Dim missing = entry.Env.Where(Function(k) Not env.ContainsKey(k.Key) AndAlso String.IsNullOrEmpty(Environment.GetEnvironmentVariable(k.Key))).ToList()
                For Each k In missing
                    Console.WriteLine($"Set {k.Key} before you use it: {k.Hint}. (Or add it again with -e {k.Key}=…)")
                Next
                Console.WriteLine("Needs " & entry.Needs & ". Test it: agentic mcp test " & fromCatalog.Id)
                Return 0
            End If
            Dim name = positional(0)
            Dim s As New McpServerOptions With {.Id = McpConfig.CleanId(name), .Name = name, .Env = env, .Headers = headers, .TimeoutSeconds = timeout, .RequireApproval = approval}
            If transport = "http" OrElse transport = "sse" Then
                Dim url = If(positional.Count > 1, positional(1), tail.FirstOrDefault())
                If String.IsNullOrWhiteSpace(url) Then Return Fail("Give the server's URL.")
                s.Transport = If(transport = "sse", McpTransportKind.Sse, McpTransportKind.Http)
                s.Endpoint = url
            Else
                Dim cmdLine = If(tail.Count > 0, tail, positional.Skip(1).ToList())
                If cmdLine.Count = 0 Then Return Fail("Give the command after --, e.g. agentic mcp add files -- npx -y @modelcontextprotocol/server-filesystem C:\work")
                s.Transport = McpTransportKind.Stdio
                s.Command = cmdLine(0)
                s.Args = cmdLine.Skip(1).ToList()
            End If
            McpConfig.Upsert(s, scope, cwd)
            Console.WriteLine($"Added {s.Id} ({s.TransportName()}, {scope}) to {If(scope = "project", McpConfig.ProjectFile(cwd), Paths.UserMcpFile())}.")
            If scope = "project" Then Console.WriteLine("Project servers are approved once per person when a session starts.")
            Return 0
        End Function

        Private Shared Function Fail(message As String) As Integer
            Console.Error.WriteLine(message)
            Return 1
        End Function

    End Class

    ''' <summary>agentic config …: read and change settings files.</summary>
    Friend NotInheritable Class ConfigCli

        Private Sub New()
        End Sub

        Public Shared Function Run(args As List(Of String)) As Integer
            Dim settings = Core.Settings.Load(Environment.CurrentDirectory)
            Dim scope = "user"
            Dim list = args.ToList()
            For i = list.Count - 1 To 0 Step -1
                If list(i) = "-g" OrElse list(i) = "--global" Then
                    scope = "user"
                    list.RemoveAt(i)
                ElseIf (list(i) = "-s" OrElse list(i) = "--scope") AndAlso i + 1 < list.Count Then
                    scope = list(i + 1).ToLowerInvariant()
                    list.RemoveRange(i, 2)
                ElseIf list(i) = "--project" OrElse list(i) = "--local" OrElse list(i) = "--user" Then
                    scope = list(i).Substring(2)
                    list.RemoveAt(i)
                End If
            Next
            Dim verb = If(list.Count > 0, list(0).ToLowerInvariant(), "list")
            Select Case verb
                Case "list", "ls"
                    For Each s In settings.Scopes.Where(Function(x) x.File IsNot Nothing)
                        Console.WriteLine($"# {s.Name}: {s.File}{If(File.Exists(s.File), "", " (not created)")}")
                        If s.Root.Count > 0 Then Console.WriteLine(s.Root.ToJsonString(Jx.PrettyOptions))
                    Next
                    Return 0
                Case "get"
                    If list.Count < 2 Then Return 2
                    Dim v = settings.Value(list(1))
                    Console.WriteLine(If(v Is Nothing, "(not set)", v.ToJsonString(Jx.PrettyOptions)))
                    Return 0
                Case "set"
                    If list.Count < 3 Then Return Usage()
                    settings.SetValue(scope, list(1), Agent.Session.ConfigValue(String.Join(" ", list.Skip(2))))
                    Console.WriteLine($"Set {list(1)} in {settings.Scope(scope).File}")
                    Return 0
                Case "add"
                    If list.Count < 3 Then Return Usage()
                    For Each v In list.Skip(2)
                        settings.AddToList(scope, list(1), v)
                    Next
                    Console.WriteLine($"Added to {list(1)} in {settings.Scope(scope).File}")
                    Return 0
                Case "remove", "rm"
                    If list.Count < 3 Then Return Usage()
                    Dim any = False
                    For Each v In list.Skip(2)
                        any = settings.RemoveFromList(scope, list(1), v) OrElse any
                    Next
                    Console.WriteLine(If(any, "Removed.", "Not found."))
                    Return If(any, 0, 1)
                Case "unset"
                    If list.Count < 2 Then Return Usage()
                    settings.SetValue(scope, list(1), Nothing)
                    Console.WriteLine($"Removed {list(1)} from {settings.Scope(scope).File}")
                    Return 0
                Case Else
                    Return Usage()
            End Select
        End Function

        Private Shared Function Usage() As Integer
            Console.WriteLine("agentic config list
agentic config get <key>                     e.g. permissions.allow
agentic config set <key> <value> [--user|--project|--local]
agentic config add <key> <value...>          add to a list (e.g. permissions.allow ""Bash(npm test:*)"")
agentic config remove <key> <value...>
agentic config unset <key>")
            Return 2
        End Function

    End Class

End Namespace
