Option Strict On
Option Explicit On
Option Infer On

' Local MCP (Model Context Protocol) support: server options from mcp.json, status snapshots and tool results.

Imports System.Text.Json

Namespace Mcp

    Public Enum McpTransportKind
        Stdio
        ''' <summary>Streamable HTTP (one endpoint, POST; answers as JSON or an event stream).</summary>
        Http
        ''' <summary>The older HTTP+SSE transport (GET an event stream that names the POST endpoint).</summary>
        Sse
    End Enum

    Public Class McpServerOptions

        Public Property Id As String
        Public Property Name As String
        Public Property Transport As McpTransportKind = McpTransportKind.Stdio
        ''' <summary>stdio: the program (node, python, uvx, npx, a path…).</summary>
        Public Property Command As String
        Public Property Args As New List(Of String)
        ''' <summary>stdio: working directory (optional).</summary>
        Public Property Cwd As String
        ''' <summary>http / sse: the endpoint URL.</summary>
        Public Property Endpoint As String
        ''' <summary>http / sse: extra headers (e.g. Authorization).</summary>
        Public Property Headers As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
        Public Property Env As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
        Public Property Enabled As Boolean = True
        Public Property TimeoutSeconds As Integer = 60
        ''' <summary>Ask before the chat model runs this server's tools.</summary>
        Public Property RequireApproval As Boolean
        ''' <summary>
        ''' Where it is configured: user (~\.agentic\mcp.json), project (.mcp.json), flag (--mcp-config), and - used as they are
        ''' there - claude (~\.claude.json), claude-local (this project in ~\.claude.json), plugin (a Claude Code plugin),
        ''' claude-desktop (Claude Desktop's config).
        ''' </summary>
        Public Property Scope As String = "user"
        ''' <summary>The source shown to the user ("Claude Code plugin github", "Project .mcp.json" …).</summary>
        Public Property Source As String
        ''' <summary>A plugin's folder (${CLAUDE_PLUGIN_ROOT}).</summary>
        Public Property PluginRoot As String
        ''' <summary>The project folder of a project's (or Claude Code's per-project) server.</summary>
        Public Property Root As String
        ''' <summary>The server's name in its file (the id can differ, e.g. plugin_x_y).</summary>
        Public Property Key As String
        ''' <summary>stdio: a .env file whose KEY=value lines go to the server's environment.</summary>
        Public Property EnvFile As String
        ''' <summary>OAuth settings from "oauth": {clientId, clientSecret, scope, callbackPort} (hosted servers without self-registration).</summary>
        Public Property OAuthClientId As String
        Public Property OAuthClientSecret As String
        Public Property OAuthScope As String
        Public Property OAuthCallbackPort As Integer

        ''' <summary>True when this app edits the server's file (Agentic's own and the project's .mcp.json).</summary>
        Public ReadOnly Property Editable As Boolean
            Get
                Return Scope = "user" OrElse Scope = "project"
            End Get
        End Property

        Public Function TransportName() As String
            Select Case Transport
                Case McpTransportKind.Http : Return "http"
                Case McpTransportKind.Sse : Return "sse"
                Case Else : Return "stdio"
            End Select
        End Function

        ''' <summary>The options as mcp.json stores them (secrets included: the file is the user's own).</summary>
        Public Function ToConfig() As Dictionary(Of String, Object)
            Dim d As New Dictionary(Of String, Object) From {{"id", Id}, {"name", Name}, {"transport", TransportName()}, {"enabled", Enabled},
                                                              {"timeoutSeconds", TimeoutSeconds}, {"requireApproval", RequireApproval}}
            If Transport = McpTransportKind.Stdio Then
                d("command") = Command
                d("args") = Args
                If Not String.IsNullOrWhiteSpace(Cwd) Then d("cwd") = Cwd
            Else
                d("endpoint") = Endpoint
                If Headers.Count > 0 Then d("headers") = Headers
            End If
            If Env.Count > 0 Then d("env") = Env
            If Not String.IsNullOrWhiteSpace(EnvFile) Then d("envFile") = EnvFile
            If Not String.IsNullOrWhiteSpace(OAuthClientId) Then
                Dim oauth As New Dictionary(Of String, Object) From {{"clientId", OAuthClientId}}
                If Not String.IsNullOrWhiteSpace(OAuthClientSecret) Then oauth("clientSecret") = OAuthClientSecret
                If Not String.IsNullOrWhiteSpace(OAuthScope) Then oauth("scope") = OAuthScope
                If OAuthCallbackPort > 0 Then oauth("callbackPort") = OAuthCallbackPort
                d("oauth") = oauth
            End If
            Return d
        End Function

    End Class

    Public Class McpServerStatus

        Public Property Id As String
        Public Property Name As String
        Public Property Transport As String
        Public Property Connected As Boolean
        Public Property Connecting As Boolean
        Public Property [Error] As String
        Public Property ServerInfo As String
        Public Property Tools As New List(Of McpToolInfo)
        Public Property StartedUtc As DateTime?
        Public Property LastUsedUtc As DateTime?
        Public Property Calls As Integer
        Public Property RecentLog As New List(Of String)
        ''' <summary>The server answered 401 and no sign-in works: /mcp login.</summary>
        Public Property NeedsSignIn As Boolean

        Public Function ToJson() As Object
            Return New With {
                .id = Id, .name = Name, .transport = Transport, .connected = Connected, .connecting = Connecting, .err = [Error],
                .serverInfo = ServerInfo, .tools = Tools.Select(Function(t) t.ToJson()).ToList(), .toolCount = Tools.Count,
                .startedUtc = StartedUtc, .lastUsedUtc = LastUsedUtc, .calls = Calls, .log = RecentLog.ToList(), .needsSignIn = NeedsSignIn}
        End Function

    End Class

    Public Class McpToolInfo

        Public Property Server As String
        Public Property Name As String
        Public Property Description As String
        ''' <summary>The tool's input JSON schema (raw).</summary>
        Public Property InputSchema As String

        Public Function ToJson() As Object
            Dim schema As Object = Nothing
            Try
                If Not String.IsNullOrEmpty(InputSchema) Then schema = JsonSerializer.Deserialize(Of JsonElement)(InputSchema)
            Catch
            End Try
            Return New With {.server = Server, .name = Name, .description = Description, .schema = schema}
        End Function

    End Class

    Public Class McpToolResult

        Public Property Ok As Boolean
        Public Property Content As String
        ''' <summary>Pictures the tool returned (a browser screenshot): media type and base64 data.</summary>
        Public Property Images As New List(Of (MediaType As String, Data As String))
        Public Property IsError As Boolean
        Public Property DurationMs As Long
        Public Property [Error] As String

        Public Function ToJson() As Object
            Return New With {.ok = Ok, .content = Content, .isError = IsError, .durationMs = DurationMs, .err = [Error]}
        End Function

    End Class

End Namespace
