Option Strict On
Option Explicit On
Option Infer On

Imports System.Diagnostics
Imports System.IO
Imports System.Net
Imports System.Net.Http
Imports System.Net.Sockets
Imports System.Security.Cryptography
Imports System.Text
Imports System.Text.Json
Imports System.Text.Json.Nodes

Namespace Mcp

    ''' <summary>
    ''' Sign-in for hosted MCP servers (OAuth 2.1, as the MCP specification and Claude Code do it):
    '''   - the server's 401 names its protected-resource metadata (else /.well-known/oauth-protected-resource), which names the
    '''     authorization server; that server's metadata gives the endpoints (else /authorize, /token, /register)
    '''   - the app registers itself (dynamic client registration) unless the config gives a client id
    '''   - the browser signs in (authorization code with PKCE) and comes back to a one-time listener on 127.0.0.1
    '''   - tokens are kept in ~\.agentic\mcp-auth.bin (DPAPI, this Windows user only) or mcp-auth.json elsewhere, and refreshed
    '''     before they run out
    ''' </summary>
    Public NotInheritable Class McpOAuth

        Private Sub New()
        End Sub

        ''' <summary>Opens the sign-in page (the app's browser; tests fetch it).</summary>
        Public Shared Property OpenBrowser As Func(Of String, Task) = AddressOf DefaultOpenBrowser
        ''' <summary>The token file.</summary>
        Public Shared Property StoreFile As String = DefaultStoreFile()

        Private Shared ReadOnly _lock As New Object()
        Private Shared ReadOnly _http As New HttpClient() With {.Timeout = TimeSpan.FromSeconds(30)}

        Private Shared Function DefaultStoreFile() As String
            Return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".agentic", If(OperatingSystem.IsWindows(), "mcp-auth.bin", "mcp-auth.json"))
        End Function

        Private Shared Function DefaultOpenBrowser(url As String) As Task
            Try
                If OperatingSystem.IsWindows() Then
                    Process.Start(New ProcessStartInfo(url) With {.UseShellExecute = True})
                ElseIf OperatingSystem.IsMacOS() Then
                    Process.Start("open", url)
                Else
                    Process.Start("xdg-open", url)
                End If
            Catch ex As Exception When TypeOf ex Is ComponentModel.Win32Exception OrElse TypeOf ex Is InvalidOperationException
            End Try
            Return Task.CompletedTask
        End Function

        ''' <summary>The resource a token is for: the server URL without query or fragment (RFC 8707).</summary>
        Public Shared Function CanonicalResource(url As String) As String
            Dim u = New UriBuilder(url) With {.Query = "", .Fragment = ""}
            Dim s = u.Uri.GetLeftPart(UriPartial.Path)
            Return If(u.Uri.AbsolutePath = "/", s.TrimEnd("/"c), s)
        End Function

        ' ------------------------------------------------------------------ the token file

        Private Shared Function LoadAll() As JsonObject
            SyncLock _lock
                Try
                    If Not File.Exists(StoreFile) Then Return New JsonObject()
                    Dim bytes = File.ReadAllBytes(StoreFile)
                    If OperatingSystem.IsWindows() AndAlso StoreFile.EndsWith(".bin", StringComparison.OrdinalIgnoreCase) Then bytes = ProtectedData.Unprotect(bytes, Nothing, DataProtectionScope.CurrentUser)
                    Return If(TryCast(JsonNode.Parse(Encoding.UTF8.GetString(bytes)), JsonObject), New JsonObject())
                Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is CryptographicException OrElse TypeOf ex Is JsonException OrElse TypeOf ex Is UnauthorizedAccessException
                    Return New JsonObject()
                End Try
            End SyncLock
        End Function

        Private Shared Sub SaveAll(all As JsonObject)
            SyncLock _lock
                Directory.CreateDirectory(Path.GetDirectoryName(StoreFile))
                Dim bytes = Encoding.UTF8.GetBytes(all.ToJsonString())
                If OperatingSystem.IsWindows() AndAlso StoreFile.EndsWith(".bin", StringComparison.OrdinalIgnoreCase) Then bytes = ProtectedData.Protect(bytes, Nothing, DataProtectionScope.CurrentUser)
                File.WriteAllBytes(StoreFile, bytes)
                If Not OperatingSystem.IsWindows() Then
                    Try
                        File.SetUnixFileMode(StoreFile, UnixFileMode.UserRead Or UnixFileMode.UserWrite)
                    Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
                    End Try
                End If
            End SyncLock
        End Sub

        Private Shared Function Entry(url As String) As JsonObject
            Return TryCast(LoadAll()(CanonicalResource(url)), JsonObject)
        End Function

        Private Shared Sub Store(url As String, e As JsonObject)
            Dim all = LoadAll()
            ' A copy: an entry read earlier still belongs to the object it was read from.
            If e Is Nothing Then all.Remove(CanonicalResource(url)) Else all(CanonicalResource(url)) = e.DeepClone()
            SaveAll(all)
        End Sub

        Private Shared Function S(o As JsonObject, name As String) As String
            Dim n = If(o Is Nothing, Nothing, o(name))
            If n Is Nothing Then Return Nothing
            Return If(n.GetValueKind() = JsonValueKind.String, n.GetValue(Of String)(), n.ToJsonString())
        End Function

        Public Shared Function SignedIn(url As String) As Boolean
            Return Entry(url) IsNot Nothing
        End Function

        Public Shared Sub SignOut(url As String)
            Store(url, Nothing)
        End Sub

        ''' <summary>The access token to send (refreshed when it runs out within a minute); Nothing when not signed in or refresh failed.</summary>
        Public Shared Async Function TokenAsync(url As String, Optional forceRefresh As Boolean = False) As Task(Of String)
            Dim e = Entry(url)
            If e Is Nothing Then Return Nothing
            Dim expires As Long = 0
            Long.TryParse(If(S(e, "expires_at"), "0"), expires)
            Dim nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            If Not forceRefresh AndAlso (expires = 0 OrElse expires > nowMs + 60000) Then Return S(e, "access_token")
            Dim refresh = S(e, "refresh_token")
            If String.IsNullOrEmpty(refresh) Then Return If(forceRefresh, Nothing, S(e, "access_token"))
            Try
                Dim form As New Dictionary(Of String, String) From {{"grant_type", "refresh_token"}, {"refresh_token", refresh}, {"client_id", S(e, "client_id")}, {"resource", S(e, "resource")}}
                If Not String.IsNullOrEmpty(S(e, "client_secret")) Then form("client_secret") = S(e, "client_secret")
                Dim j = Await PostFormAsync(S(e, "token_endpoint"), form).ConfigureAwait(False)
                e("access_token") = S(j, "access_token")
                If Not String.IsNullOrEmpty(S(j, "refresh_token")) Then e("refresh_token") = S(j, "refresh_token")
                e("expires_at") = ExpiresAt(j)
                Store(url, e)
                Return S(e, "access_token")
            Catch ex As Exception When TypeOf ex Is HttpRequestException OrElse TypeOf ex Is InvalidOperationException OrElse TypeOf ex Is TaskCanceledException OrElse TypeOf ex Is JsonException
                Return Nothing
            End Try
        End Function

        Private Shared Function ExpiresAt(j As JsonObject) As Long
            Dim secs As Long = 0
            If Long.TryParse(If(S(j, "expires_in"), ""), secs) AndAlso secs > 0 Then Return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + secs * 1000
            Return 0
        End Function

        ' ------------------------------------------------------------------ discovery

        Private Class Endpoints
            Public Issuer As String
            Public Authorize As String
            Public Token As String
            Public Register As String
            Public Scope As String
            Public Resource As String
        End Class

        Private Shared Async Function GetJsonAsync(url As String) As Task(Of JsonObject)
            Try
                Using req As New HttpRequestMessage(HttpMethod.Get, url)
                    req.Headers.TryAddWithoutValidation("Accept", "application/json")
                    req.Headers.TryAddWithoutValidation("MCP-Protocol-Version", "2025-06-18")
                    Using res = Await _http.SendAsync(req).ConfigureAwait(False)
                        If Not res.IsSuccessStatusCode Then Return Nothing
                        Return TryCast(JsonNode.Parse(Await res.Content.ReadAsStringAsync().ConfigureAwait(False)), JsonObject)
                    End Using
                End Using
            Catch ex As Exception When TypeOf ex Is HttpRequestException OrElse TypeOf ex Is TaskCanceledException OrElse TypeOf ex Is JsonException OrElse TypeOf ex Is UriFormatException OrElse TypeOf ex Is InvalidOperationException
                Return Nothing
            End Try
        End Function

        Private Shared Async Function DiscoverAsync(url As String, wwwAuthenticate As String) As Task(Of Endpoints)
            Dim u As New Uri(url)
            Dim origin = u.GetLeftPart(UriPartial.Authority)
            Dim p = u.AbsolutePath.TrimEnd("/"c)
            Dim named = RegularExpressions.Regex.Match(If(wwwAuthenticate, ""), "resource_metadata=""([^""]+)""", RegularExpressions.RegexOptions.IgnoreCase)
            Dim scopeHint = RegularExpressions.Regex.Match(If(wwwAuthenticate, ""), "scope=""([^""]+)""", RegularExpressions.RegexOptions.IgnoreCase)
            Dim prm As JsonObject = Nothing
            For Each c In {If(named.Success, New Uri(u, named.Groups(1).Value).ToString(), ""), If(p.Length > 0, origin & "/.well-known/oauth-protected-resource" & p, ""), origin & "/.well-known/oauth-protected-resource"}
                If c.Length = 0 Then Continue For
                prm = Await GetJsonAsync(c).ConfigureAwait(False)
                If prm IsNot Nothing Then Exit For
            Next
            Dim issuer = origin
            Dim servers = TryCast(prm?("authorization_servers"), JsonArray)
            If servers IsNot Nothing AndAlso servers.Count > 0 Then issuer = servers(0).ToString().TrimEnd("/"c)
            Dim iu As New Uri(issuer)
            Dim iorigin = iu.GetLeftPart(UriPartial.Authority)
            Dim ipath = iu.AbsolutePath.TrimEnd("/"c)
            Dim candidates = If(ipath.Length > 0,
                {iorigin & "/.well-known/oauth-authorization-server" & ipath, iorigin & "/.well-known/openid-configuration" & ipath, issuer & "/.well-known/openid-configuration"},
                {iorigin & "/.well-known/oauth-authorization-server", iorigin & "/.well-known/openid-configuration"})
            Dim meta As JsonObject = Nothing
            For Each c In candidates
                meta = Await GetJsonAsync(c).ConfigureAwait(False)
                If meta IsNot Nothing AndAlso Not String.IsNullOrEmpty(S(meta, "authorization_endpoint")) Then Exit For
                meta = Nothing
            Next
            Dim scopes As String = Nothing
            If scopeHint.Success Then
                scopes = scopeHint.Groups(1).Value
            ElseIf TypeOf prm?("scopes_supported") Is JsonArray Then
                scopes = String.Join(" ", DirectCast(prm("scopes_supported"), JsonArray).Select(Function(x) x.ToString()))
            End If
            Return New Endpoints With {
                .Issuer = issuer,
                .Authorize = If(S(meta, "authorization_endpoint"), iorigin & "/authorize"),
                .Token = If(S(meta, "token_endpoint"), iorigin & "/token"),
                .Register = If(meta Is Nothing, iorigin & "/register", S(meta, "registration_endpoint")),
                .Scope = scopes,
                .Resource = If(S(prm, "resource"), CanonicalResource(url))}
        End Function

        Private Shared Async Function PostFormAsync(url As String, form As Dictionary(Of String, String)) As Task(Of JsonObject)
            Using content As New FormUrlEncodedContent(form)
                Using res = Await _http.PostAsync(url, content).ConfigureAwait(False)
                    Dim text = Await res.Content.ReadAsStringAsync().ConfigureAwait(False)
                    Dim j As JsonObject = Nothing
                    Try
                        j = TryCast(JsonNode.Parse(text), JsonObject)
                    Catch ex As JsonException
                    End Try
                    If Not res.IsSuccessStatusCode OrElse j Is Nothing OrElse j("error") IsNot Nothing Then
                        Throw New InvalidOperationException("The token endpoint refused: " & If(S(j, "error_description"), If(S(j, "error"), "HTTP " & CInt(res.StatusCode))))
                    End If
                    Return j
                End Using
            End Using
        End Function

        Private Shared Function B64Url(b As Byte()) As String
            Return Convert.ToBase64String(b).Replace("+"c, "-"c).Replace("/"c, "_"c).TrimEnd("="c)
        End Function

        ' ------------------------------------------------------------------ signing in

        ''' <summary>Signs in through the browser and keeps the tokens; returns the access token.</summary>
        Public Shared Async Function SignInAsync(url As String, wwwAuthenticate As String, Optional clientId As String = Nothing, Optional clientSecret As String = Nothing,
                                                 Optional scope As String = Nothing, Optional callbackPort As Integer = 0, Optional timeout As TimeSpan? = Nothing) As Task(Of String)
            Dim ep = Await DiscoverAsync(url, wwwAuthenticate).ConfigureAwait(False)
            Dim listener As New TcpListener(IPAddress.Loopback, callbackPort)
            listener.Start()
            Try
                Dim port = DirectCast(listener.LocalEndpoint, IPEndPoint).Port
                Dim redirect = $"http://127.0.0.1:{port}/callback"
                If String.IsNullOrEmpty(clientId) Then
                    If String.IsNullOrEmpty(ep.Register) Then Throw New InvalidOperationException("This server does not let apps register themselves: give ""oauth"": {""clientId"": …} in its config.")
                    Dim reg As New JsonObject From {{"client_name", "Agentic"}, {"redirect_uris", New JsonArray(JsonValue.Create(redirect))},
                                                    {"grant_types", New JsonArray(JsonValue.Create("authorization_code"), JsonValue.Create("refresh_token"))},
                                                    {"response_types", New JsonArray(JsonValue.Create("code"))}, {"token_endpoint_auth_method", "none"}}
                    Using content As New StringContent(reg.ToJsonString(), Encoding.UTF8, "application/json")
                        Using res = Await _http.PostAsync(ep.Register, content).ConfigureAwait(False)
                            Dim j As JsonObject = Nothing
                            Try
                                j = TryCast(JsonNode.Parse(Await res.Content.ReadAsStringAsync().ConfigureAwait(False)), JsonObject)
                            Catch ex As JsonException
                            End Try
                            If Not res.IsSuccessStatusCode OrElse String.IsNullOrEmpty(S(j, "client_id")) Then
                                Throw New InvalidOperationException("The server refused to register Agentic: " & If(S(j, "error_description"), If(S(j, "error"), "HTTP " & CInt(res.StatusCode))))
                            End If
                            clientId = S(j, "client_id")
                            If Not String.IsNullOrEmpty(S(j, "client_secret")) Then clientSecret = S(j, "client_secret")
                        End Using
                    End Using
                End If
                Dim verifier = B64Url(RandomNumberGenerator.GetBytes(48))
                Dim challenge = B64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))
                Dim state = B64Url(RandomNumberGenerator.GetBytes(16))
                Dim q As New List(Of String) From {
                    "response_type=code", "client_id=" & Uri.EscapeDataString(clientId), "redirect_uri=" & Uri.EscapeDataString(redirect),
                    "code_challenge=" & challenge, "code_challenge_method=S256", "state=" & state, "resource=" & Uri.EscapeDataString(ep.Resource)}
                Dim sc = If(scope, ep.Scope)
                If Not String.IsNullOrEmpty(sc) Then q.Add("scope=" & Uri.EscapeDataString(sc))
                Dim authUrl = ep.Authorize & If(ep.Authorize.Contains("?"c), "&", "?") & String.Join("&", q)
                Dim wait = WaitForCodeAsync(listener, state, If(timeout, TimeSpan.FromMinutes(5)))
                Await OpenBrowser(authUrl).ConfigureAwait(False)
                Dim code = Await wait.ConfigureAwait(False)
                Dim form As New Dictionary(Of String, String) From {{"grant_type", "authorization_code"}, {"code", code}, {"redirect_uri", redirect},
                                                                    {"client_id", clientId}, {"code_verifier", verifier}, {"resource", ep.Resource}}
                If Not String.IsNullOrEmpty(clientSecret) Then form("client_secret") = clientSecret
                Dim tok = Await PostFormAsync(ep.Token, form).ConfigureAwait(False)
                If String.IsNullOrEmpty(S(tok, "access_token")) Then Throw New InvalidOperationException("The token endpoint sent no access token.")
                Dim e As New JsonObject From {{"access_token", S(tok, "access_token")}, {"refresh_token", S(tok, "refresh_token")}, {"expires_at", ExpiresAt(tok)},
                                              {"client_id", clientId}, {"client_secret", clientSecret}, {"token_endpoint", ep.Token}, {"resource", ep.Resource}, {"issuer", ep.Issuer}}
                Store(url, e)
                Return S(e, "access_token")
            Finally
                listener.Stop()
            End Try
        End Function

        ''' <summary>The browser's redirect to http://127.0.0.1:port/callback?code=…&amp;state=…</summary>
        Private Shared Async Function WaitForCodeAsync(listener As TcpListener, state As String, timeout As TimeSpan) As Task(Of String)
            Using cts As New Threading.CancellationTokenSource(timeout)
                While True
                    Dim client As TcpClient
                    Try
                        client = Await listener.AcceptTcpClientAsync(cts.Token).ConfigureAwait(False)
                    Catch ex As OperationCanceledException
                        Throw New TimeoutException($"The sign-in was not finished within {CInt(timeout.TotalMinutes)} minutes.")
                    End Try
                    Using client
                        Dim stream = client.GetStream()
                        Dim reader As New StreamReader(stream, Encoding.ASCII, False, 4096, leaveOpen:=True)
                        Dim first = Await reader.ReadLineAsync(cts.Token).ConfigureAwait(False)
                        Dim target = If(first, "").Split(" "c).Skip(1).FirstOrDefault()
                        Dim query = Web.HttpUtility.ParseQueryString(If(target IsNot Nothing AndAlso target.Contains("?"c), target.Substring(target.IndexOf("?"c) + 1), ""))
                        Dim isCallback = target IsNot Nothing AndAlso target.StartsWith("/callback", StringComparison.Ordinal)
                        Dim code = query("code")
                        Dim body = If(isCallback AndAlso Not String.IsNullOrEmpty(code), "<b>Signed in.</b> You can close this tab and go back to Agentic.",
                                      "<b>Sign-in failed:</b> " & WebUtility.HtmlEncode(If(query("error_description"), If(query("error"), "no code"))))
                        Dim html = "<!doctype html><meta charset=""utf-8""><title>Agentic</title><body style=""font:16px system-ui;padding:40px"">" & body & "</body>"
                        Dim bytes = Encoding.UTF8.GetBytes(html)
                        Dim head = Encoding.ASCII.GetBytes($"HTTP/1.1 {If(isCallback, "200 OK", "404 Not Found")}{vbCrLf}Content-Type: text/html; charset=utf-8{vbCrLf}Content-Length: {bytes.Length}{vbCrLf}Connection: close{vbCrLf}{vbCrLf}")
                        Await stream.WriteAsync(head, cts.Token).ConfigureAwait(False)
                        Await stream.WriteAsync(bytes, cts.Token).ConfigureAwait(False)
                        If Not isCallback OrElse query("state") <> state Then Continue While
                        If String.IsNullOrEmpty(code) Then Throw New InvalidOperationException("Sign-in refused: " & If(query("error_description"), If(query("error"), "no code")))
                        Return code
                    End Using
                End While
            End Using
            Return Nothing
        End Function

    End Class

End Namespace
