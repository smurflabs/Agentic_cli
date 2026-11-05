Option Strict On
Option Explicit On
Option Infer On

Imports System.IO
Imports System.Text.Json
Imports System.Text.RegularExpressions
Imports AgenticCli.Core

Namespace Mcp

    ''' <summary>
    ''' MCP servers for the CLI, in either layout ({"servers": [...]} or Claude Code's {"mcpServers": {...}}):
    '''   user     ~\.agentic\mcp.json
    '''   project  &lt;project&gt;\.mcp.json (Claude Code's project file; each server must be approved once)
    '''   flag     --mcp-config &lt;file or JSON&gt;
    ''' and, used as they are there (includeClaudeConfig, on by default): Claude Code's ~\.claude.json (user servers and this
    ''' project's), the servers of installed Claude Code plugins, and Claude Desktop's config. They can also be copied into
    ''' ~\.agentic\mcp.json (agentic mcp import).
    ''' </summary>
    Friend NotInheritable Class McpConfig

        Private Sub New()
        End Sub

        Public Shared Function ProjectFile(projectDir As String) As String
            Return Path.Combine(projectDir, ".mcp.json")
        End Function

        Public Shared Function ClaudeDesktopFile() As String
            Dim home = Paths.Home()
            If OperatingSystem.IsWindows() Then Return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude", "claude_desktop_config.json")
            If OperatingSystem.IsMacOS() Then Return Path.Combine(home, "Library", "Application Support", "Claude", "claude_desktop_config.json")
            Return Path.Combine(home, ".config", "Claude", "claude_desktop_config.json")
        End Function

        Private Shared Function SamePath(a As String, b As String) As Boolean
            Dim n = Function(p As String) Path.GetFullPath(p.Replace("/"c, Path.DirectorySeparatorChar)).TrimEnd(Path.DirectorySeparatorChar)
            Try
                Return String.Equals(n(a), n(b), If(OperatingSystem.IsLinux(), StringComparison.Ordinal, StringComparison.OrdinalIgnoreCase))
            Catch ex As Exception When TypeOf ex Is ArgumentException OrElse TypeOf ex Is NotSupportedException OrElse TypeOf ex Is PathTooLongException
                Return False
            End Try
        End Function

        ''' <summary>Every configured server; a later scope wins per id (Agentic's own files over Claude's).</summary>
        Public Shared Function Load(projectDir As String, flagConfigs As IEnumerable(Of String), Optional includeClaude As Boolean = True) As List(Of McpServerOptions)
            Dim servers As New List(Of McpServerOptions)
            Dim add = Sub(list As List(Of McpServerOptions), scope As String, source As String)
                          For Each s In list
                              s.Scope = scope
                              s.Source = source
                              servers.RemoveAll(Function(x) x.Id.Equals(s.Id, StringComparison.OrdinalIgnoreCase))
                              servers.Add(s)
                          Next
                      End Sub
            ' Claude Code plugins that are on (/plugin): Agentic's own always, Claude Code's with its other files.
            For Each p In ClaudePlugins(projectDir, includeClaude)
                add(p.Servers, "plugin", "Claude Code plugin " & p.Plugin)
            Next
            If includeClaude Then
                add(Parse(Paths.ReadText(ClaudeDesktopFile())), "claude-desktop", "Claude Desktop")
                Dim claudeJson = Paths.ReadText(Path.Combine(Paths.Home(), ".claude.json"))
                If claudeJson IsNot Nothing Then
                    add(Parse(claudeJson), "claude", "Claude Code")
                    Try
                        Using doc = JsonDocument.Parse(claudeJson, Jx.Lenient)
                            Dim projects As JsonElement
                            If doc.RootElement.TryGetProperty("projects", projects) AndAlso projects.ValueKind = JsonValueKind.Object Then
                                For Each p In projects.EnumerateObject()
                                    If SamePath(p.Name, projectDir) Then add(Parse(p.Value.GetRawText()), "claude-local", "Claude Code (this project)")
                                Next
                            End If
                        End Using
                    Catch ex As JsonException
                    End Try
                End If
            End If
            add(Parse(Paths.ReadText(Paths.UserMcpFile())), "user", "Agentic")
            add(Parse(Paths.ReadText(ProjectFile(projectDir))), "project", "Project .mcp.json")
            For Each f In If(flagConfigs, Enumerable.Empty(Of String)())
                add(Parse(If(File.Exists(f), Paths.ReadText(f), f)), "flag", "--mcp-config")
            Next
            Return servers
        End Function

        ''' <summary>
        ''' The MCP servers of the Claude Code plugins that are on (/plugin): those installed with Agentic, and Claude Code's
        ''' own (~\.claude\plugins\installed_plugins.json) when includeClaude; Agentic's on/off wins over Claude Code's.
        ''' </summary>
        Public Shared Function ClaudePlugins(projectDir As String, Optional includeClaude As Boolean = True) As List(Of (Plugin As String, Servers As List(Of McpServerOptions)))
            Dim found As New List(Of (String, List(Of McpServerOptions)))
            For Each p In Core.Plugins.Enabled({projectDir}).Where(Function(x) includeClaude OrElse x.Source = "Agentic")
                Dim plugin = p.Id.Split("@"c)(0)
                Dim dir = p.Dir
                Try
                    Dim list = Parse(Paths.ReadText(Path.Combine(dir, ".mcp.json")))
                    Dim manifest = Paths.ReadText(Path.Combine(dir, ".claude-plugin", "plugin.json"))
                    If manifest IsNot Nothing Then
                        Using mdoc = JsonDocument.Parse(manifest, Jx.Lenient)
                            Dim ms As JsonElement
                            If mdoc.RootElement.ValueKind = JsonValueKind.Object AndAlso mdoc.RootElement.TryGetProperty("mcpServers", ms) Then
                                If ms.ValueKind = JsonValueKind.String Then
                                    list.AddRange(Parse(Paths.ReadText(Path.GetFullPath(Path.Combine(dir, ms.GetString())))))
                                ElseIf ms.ValueKind = JsonValueKind.Object Then
                                    list.AddRange(Parse("{""mcpServers"":" & ms.GetRawText() & "}"))
                                End If
                            End If
                        End Using
                    End If
                    For Each s In list
                        s.PluginRoot = dir
                        s.Id = CleanId("plugin_" & plugin & "_" & s.Key)
                    Next
                    If list.Count > 0 Then found.Add((plugin, list))
                Catch ex As JsonException
                End Try
            Next
            Return found
        End Function

        ''' <summary>
        ''' A value with its variables filled in: ${VAR} and ${VAR:-default} (Claude Code), ${env:VAR}, ${CLAUDE_PLUGIN_ROOT},
        ''' ${CLAUDE_PROJECT_DIR} / ${workspaceFolder}, ${userHome}, and %VAR% on Windows. Variables that are not set are added
        ''' to missing (and become empty).
        ''' </summary>
        Public Shared Function Expand(value As String, Optional projectDir As String = Nothing, Optional pluginRoot As String = Nothing,
                                      Optional extra As IDictionary(Of String, String) = Nothing, Optional missing As List(Of String) = Nothing) As String
            If String.IsNullOrEmpty(value) Then Return If(value, "")
            Dim lookup = Function(name As String) As String
                             Dim v As String = Nothing
                             If extra IsNot Nothing AndAlso extra.TryGetValue(name, v) Then Return v
                             Return Environment.GetEnvironmentVariable(name)
                         End Function
            Dim result = Regex.Replace(value, "\$\{([^}]+)\}",
                Function(m)
                    Dim body = m.Groups(1).Value
                    Dim i = body.IndexOf(":-", StringComparison.Ordinal)
                    Dim name = If(i >= 0, body.Substring(0, i), body)
                    Dim def = If(i >= 0, body.Substring(i + 2), Nothing)
                    Select Case name
                        Case "CLAUDE_PLUGIN_ROOT" : Return If(pluginRoot, "")
                        Case "CLAUDE_PROJECT_DIR", "workspaceFolder" : Return If(projectDir, Environment.CurrentDirectory)
                        Case "userHome" : Return Paths.Home()
                        Case "pathSeparator", "/" : Return Path.DirectorySeparatorChar.ToString()
                    End Select
                    If name.StartsWith("env:", StringComparison.Ordinal) Then name = name.Substring(4)
                    Dim v = lookup(name)
                    If Not String.IsNullOrEmpty(v) Then Return v
                    If def IsNot Nothing Then Return def
                    missing?.Add(name)
                    Return ""
                End Function)
            If OperatingSystem.IsWindows() Then
                result = Regex.Replace(result, "%([A-Za-z_][A-Za-z0-9_]*)%", Function(m) If(lookup(m.Groups(1).Value), m.Value))
            End If
            Return result
        End Function

        ''' <summary>KEY=value lines of a .env file.</summary>
        Public Shared Function ReadEnvFile(file As String) As Dictionary(Of String, String)
            Dim d As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
            Dim text = Paths.ReadText(file)
            If text Is Nothing Then Return d
            For Each line In text.Split(ChrW(10))
                Dim m = Regex.Match(line, "^\s*(?:export\s+)?([A-Za-z_][A-Za-z0-9_.]*)\s*=\s*(.*?)\s*$")
                If m.Success Then d(m.Groups(1).Value) = Regex.Replace(m.Groups(2).Value, "^(['""])(.*)\1$", "$2")
            Next
            Return d
        End Function

        ''' <summary>Servers from a JSON text in either layout (unknown or broken entries are skipped).</summary>
        Public Shared Function Parse(json As String) As List(Of McpServerOptions)
            Dim result As New List(Of McpServerOptions)
            If String.IsNullOrWhiteSpace(json) Then Return result
            Try
                Using doc = JsonDocument.Parse(json, Jx.Lenient)
                    Dim root = doc.RootElement
                    Dim el As JsonElement
                    If root.ValueKind <> JsonValueKind.Object Then Return result
                    If root.TryGetProperty("servers", el) AndAlso el.ValueKind = JsonValueKind.Array Then
                        For Each s In el.EnumerateArray()
                            Dim o = FromElement(s, JsonStr(s, "id", Nothing))
                            If o IsNot Nothing Then result.Add(o)
                        Next
                    End If
                    ' VS Code's mcp.json: {"servers": {"name": {…}}}.
                    If root.TryGetProperty("servers", el) AndAlso el.ValueKind = JsonValueKind.Object Then
                        For Each p In el.EnumerateObject()
                            Dim o = FromElement(p.Value, p.Name)
                            If o IsNot Nothing Then result.Add(o)
                        Next
                    End If
                    If root.TryGetProperty("mcpServers", el) AndAlso el.ValueKind = JsonValueKind.Object Then
                        For Each p In el.EnumerateObject()
                            Dim o = FromElement(p.Value, p.Name)
                            If o IsNot Nothing Then result.Add(o)
                        Next
                    End If
                End Using
            Catch ex As JsonException
            End Try
            Return result
        End Function

        Private Shared Function FromElement(s As JsonElement, id As String) As McpServerOptions
            If s.ValueKind <> JsonValueKind.Object OrElse String.IsNullOrWhiteSpace(id) Then Return Nothing
            Dim o As New McpServerOptions With {
                .Id = CleanId(id), .Name = JsonStr(s, "name", id), .Command = JsonStr(s, "command", Nothing),
                .Endpoint = If(JsonStr(s, "endpoint", Nothing), JsonStr(s, "url", Nothing)), .Cwd = JsonStr(s, "cwd", Nothing),
                .Enabled = JsonBool(s, "enabled", Not JsonBool(s, "disabled", False)), .TimeoutSeconds = JsonInt(s, "timeoutSeconds", 60),
                .RequireApproval = JsonBool(s, "requireApproval", False), .Key = id, .EnvFile = JsonStr(s, "envFile", Nothing)}
            Dim oauth As JsonElement
            If s.TryGetProperty("oauth", oauth) AndAlso oauth.ValueKind = JsonValueKind.Object Then
                o.OAuthClientId = JsonStr(oauth, "clientId", Nothing)
                o.OAuthClientSecret = JsonStr(oauth, "clientSecret", Nothing)
                o.OAuthScope = JsonStr(oauth, "scope", Nothing)
                o.OAuthCallbackPort = JsonInt(oauth, "callbackPort", 0)
            End If
            Dim transport = If(JsonStr(s, "transport", Nothing), JsonStr(s, "type", Nothing))
            transport = If(transport, If(o.Endpoint IsNot Nothing, "http", "stdio")).ToLowerInvariant()
            o.Transport = If(transport = "sse", McpTransportKind.Sse, If(transport = "http" OrElse transport = "streamable-http" OrElse transport = "streamablehttp", McpTransportKind.Http, McpTransportKind.Stdio))
            If o.Transport = McpTransportKind.Stdio AndAlso String.IsNullOrWhiteSpace(o.Command) Then Return Nothing
            If o.Transport <> McpTransportKind.Stdio AndAlso String.IsNullOrWhiteSpace(o.Endpoint) Then Return Nothing
            Dim el As JsonElement
            If s.TryGetProperty("args", el) AndAlso el.ValueKind = JsonValueKind.Array Then o.Args = el.EnumerateArray().Select(Function(a) a.ToString()).ToList()
            If s.TryGetProperty("env", el) AndAlso el.ValueKind = JsonValueKind.Object Then
                For Each p In el.EnumerateObject()
                    o.Env(p.Name) = p.Value.ToString()
                Next
            End If
            If s.TryGetProperty("headers", el) AndAlso el.ValueKind = JsonValueKind.Object Then
                For Each p In el.EnumerateObject()
                    o.Headers(p.Name) = p.Value.ToString()
                Next
            End If
            Return o
        End Function

        Public Shared Function CleanId(id As String) As String
            Dim clean = New String(If(id, "").Trim().Select(Function(c) If(Char.IsLetterOrDigit(c) OrElse c = "-"c OrElse c = "_"c, Char.ToLowerInvariant(c), "-"c)).ToArray()).Trim("-"c)
            Return If(clean.Length = 0, "server", If(clean.Length > 40, clean.Substring(0, 40), clean))
        End Function

        ' ------------------------------------------------------------------ editing (user or project file, Claude Code layout)

        Private Shared Function FileFor(scope As String, projectDir As String) As String
            Return If(scope = "project", ProjectFile(projectDir), Paths.UserMcpFile())
        End Function

        Public Shared Sub Upsert(server As McpServerOptions, scope As String, projectDir As String)
            Dim file = FileFor(scope, projectDir)
            Dim list = Parse(Paths.ReadText(file))
            list.RemoveAll(Function(x) x.Id.Equals(server.Id, StringComparison.OrdinalIgnoreCase))
            list.Add(server)
            Save(file, list)
        End Sub

        Public Shared Function Remove(id As String, scope As String, projectDir As String) As Boolean
            Dim file = FileFor(scope, projectDir)
            Dim list = Parse(Paths.ReadText(file))
            Dim removed = list.RemoveAll(Function(x) x.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) > 0
            If removed Then Save(file, list)
            Return removed
        End Function

        Public Shared Sub SetEnabled(id As String, enabled As Boolean, scope As String, projectDir As String)
            Dim file = FileFor(scope, projectDir)
            Dim list = Parse(Paths.ReadText(file))
            Dim s = list.FirstOrDefault(Function(x) x.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
            If s Is Nothing Then Return
            s.Enabled = enabled
            Save(file, list)
        End Sub

        Private Shared Sub Save(file As String, list As List(Of McpServerOptions))
            Dim servers As New Dictionary(Of String, Object)
            For Each s In list
                Dim d As New Dictionary(Of String, Object)
                If s.Transport = McpTransportKind.Stdio Then
                    d("command") = s.Command
                    If s.Args.Count > 0 Then d("args") = s.Args
                    If Not String.IsNullOrWhiteSpace(s.Cwd) Then d("cwd") = s.Cwd
                Else
                    d("type") = s.TransportName()
                    d("url") = s.Endpoint
                    If s.Headers.Count > 0 Then d("headers") = s.Headers
                End If
                If s.Env.Count > 0 Then d("env") = s.Env
                If Not s.Enabled Then d("disabled") = True
                If s.RequireApproval Then d("requireApproval") = True
                If s.TimeoutSeconds <> 60 Then d("timeoutSeconds") = s.TimeoutSeconds
                If Not String.IsNullOrWhiteSpace(s.EnvFile) Then d("envFile") = s.EnvFile
                If Not String.IsNullOrWhiteSpace(s.OAuthClientId) Then
                    Dim oauth As New Dictionary(Of String, Object) From {{"clientId", s.OAuthClientId}}
                    If Not String.IsNullOrWhiteSpace(s.OAuthClientSecret) Then oauth("clientSecret") = s.OAuthClientSecret
                    If Not String.IsNullOrWhiteSpace(s.OAuthScope) Then oauth("scope") = s.OAuthScope
                    If s.OAuthCallbackPort > 0 Then oauth("callbackPort") = s.OAuthCallbackPort
                    d("oauth") = oauth
                End If
                servers(s.Id) = d
            Next
            Paths.WriteText(file, Jx.Stringify(New Dictionary(Of String, Object) From {{"mcpServers", servers}}, True) & Environment.NewLine)
        End Sub

        ''' <summary>Servers configured in Claude Code (~\.claude.json, user and project entries) and Claude Desktop.</summary>
        Public Shared Function FindClaudeServers() As List(Of (Source As String, Server As McpServerOptions))
            Dim found As New List(Of (String, McpServerOptions))
            Dim claudeJson = Paths.ReadText(Path.Combine(Paths.Home(), ".claude.json"))
            If claudeJson IsNot Nothing Then
                For Each s In Parse(claudeJson)
                    found.Add(("Claude Code", s))
                Next
                Try
                    Using doc = JsonDocument.Parse(claudeJson, Jx.Lenient)
                        Dim projects As JsonElement
                        If doc.RootElement.TryGetProperty("projects", projects) AndAlso projects.ValueKind = JsonValueKind.Object Then
                            For Each p In projects.EnumerateObject()
                                For Each s In Parse(p.Value.GetRawText())
                                    found.Add(("Claude Code · " & Path.GetFileName(p.Name.TrimEnd("/"c, "\"c)), s))
                                Next
                            Next
                        End If
                    End Using
                Catch ex As JsonException
                End Try
            End If
            For Each s In Parse(Paths.ReadText(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude", "claude_desktop_config.json")))
                found.Add(("Claude Desktop", s))
            Next
            For Each s In Parse(Paths.ReadText(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AgenticClient", "mcp.json")))
                found.Add(("Agentic Desktop", s))
            Next
            Return found
        End Function

        Friend Shared Function JsonStr(e As JsonElement, name As String, fallback As String) As String
            Dim el As JsonElement
            If e.TryGetProperty(name, el) AndAlso el.ValueKind = JsonValueKind.String Then Return el.GetString()
            Return fallback
        End Function

        Friend Shared Function JsonBool(e As JsonElement, name As String, fallback As Boolean) As Boolean
            Dim el As JsonElement
            If Not e.TryGetProperty(name, el) Then Return fallback
            If el.ValueKind = JsonValueKind.True Then Return True
            If el.ValueKind = JsonValueKind.False Then Return False
            Return fallback
        End Function

        Friend Shared Function JsonInt(e As JsonElement, name As String, fallback As Integer) As Integer
            Dim el As JsonElement
            Dim v As Integer
            If e.TryGetProperty(name, el) AndAlso el.ValueKind = JsonValueKind.Number AndAlso el.TryGetInt32(v) Then Return v
            Return fallback
        End Function

    End Class

End Namespace
