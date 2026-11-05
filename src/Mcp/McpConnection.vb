Option Strict On
Option Explicit On
Option Infer On

Imports System.Collections.Concurrent
Imports System.Diagnostics
Imports System.IO
Imports System.Net.Http
Imports System.Text
Imports System.Text.Json
Imports System.Text.Json.Nodes
Imports System.Threading
Imports AgenticCli.Core

Namespace Mcp

    ''' <summary>
    ''' One MCP server. stdio servers run as child processes speaking newline-delimited JSON-RPC 2.0; a background reader
    ''' matches answers to requests by id, answers the server's own requests (ping, roots/list) and keeps the last lines of
    ''' stderr for diagnostics. HTTP servers use Streamable HTTP (with its session id) or the older HTTP+SSE transport.
    ''' </summary>
    Public Class McpConnection
        Implements IDisposable

        Public ReadOnly Options As McpServerOptions
        Public ReadOnly Status As New McpServerStatus()

        Private _process As Process
        Private _http As HttpClient
        Private _sessionId As String
        Private _protocolVersion As String = "2025-03-26"
        Private _ssePostUrl As String
        Private _sseCts As CancellationTokenSource
        Private _nextId As Integer
        Private ReadOnly _pending As New ConcurrentDictionary(Of Integer, TaskCompletionSource(Of JsonElement))()
        Private ReadOnly _writeLock As New SemaphoreSlim(1, 1)
        Private _disposed As Boolean
        ''' <summary>The endpoint with its variables filled in.</summary>
        Private _endpoint As String
        ''' <summary>The OAuth access token sent when the config gives no Authorization header.</summary>
        Private _bearer As String
        Private _explicitAuth As Boolean
        ''' <summary>Variables the config uses that are not set (named when the server fails to start).</summary>
        Private ReadOnly _missing As New List(Of String)
        ''' <summary>The values of the server's env (and envFile), which its other values may use too.</summary>
        Private ReadOnly _vars As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
        ''' <summary>The last 401's WWW-Authenticate header (where to sign in).</summary>
        Public Property WwwAuthenticate As String
        ''' <summary>What the server said it offers (initialize): tools, resources, prompts.</summary>
        Public Property Capabilities As JsonElement
        ''' <summary>Its prompts (name, description, arguments), offered as /mcp__server__prompt commands.</summary>
        Public Property Prompts As New List(Of (Name As String, Description As String, Args As List(Of (Name As String, Required As Boolean))))
        ''' <summary>
        ''' Asks the user what a server wants to know while a tool runs (MCP elicitation): server, message, the fields (a JSON
        ''' schema). Nothing: such questions are declined. Answer: {"action": "accept", "content": {…}} or decline / cancel.
        ''' </summary>
        Public Shared Property Elicit As Func(Of String, String, JsonElement, Task(Of JsonObject))

        Public Function HasCapability(name As String) As Boolean
            Dim el As JsonElement
            Return Capabilities.ValueKind = JsonValueKind.Object AndAlso Capabilities.TryGetProperty(name, el)
        End Function

        Public Event ToolsChanged As EventHandler
        ''' <summary>Folders the server may work in (answered to roots/list): the working directories.</summary>
        Public Property Roots As New List(Of String)

        Public Sub New(options As McpServerOptions)
            Me.Options = options
            Status.Id = options.Id
            Status.Name = options.Name
            Status.Transport = options.TransportName()
        End Sub

        ''' <summary>A config value with its variables filled in (${VAR}, ${VAR:-default}, ${CLAUDE_PLUGIN_ROOT}, %VAR% …).</summary>
        Private Function X(value As String) As String
            Return McpConfig.Expand(value, Roots.FirstOrDefault(), Options.PluginRoot, _vars, _missing)
        End Function

        Public Async Function StartAsync() As Task
            Status.Connecting = True
            Status.NeedsSignIn = False
            Try
                _missing.Clear()
                _vars.Clear()
                If Not String.IsNullOrWhiteSpace(Options.EnvFile) Then
                    For Each kv In McpConfig.ReadEnvFile(X(Options.EnvFile))
                        _vars(kv.Key) = kv.Value
                    Next
                End If
                For Each kv In Options.Env
                    _vars(kv.Key) = X(kv.Value)
                Next
                If Options.Transport <> McpTransportKind.Stdio Then
                    _endpoint = X(Options.Endpoint)
                    _explicitAuth = Options.Headers.Keys.Any(Function(k) k.Equals("Authorization", StringComparison.OrdinalIgnoreCase) AndAlso
                                                                         X(Options.Headers(k)).Trim().Length > "Bearer".Length)
                    If Not _explicitAuth Then _bearer = Await McpOAuth.TokenAsync(_endpoint).ConfigureAwait(False)
                End If
                Select Case Options.Transport
                    Case McpTransportKind.Stdio : StartStdio()
                    Case McpTransportKind.Sse : Await StartSseAsync().ConfigureAwait(False)
                    Case Else : StartHttp()
                End Select
                Dim init = Await RequestAsync("initialize", New With {
                    .protocolVersion = _protocolVersion,
                    .capabilities = If(Elicit Is Nothing, CObj(New With {.roots = New With {.listChanged = False}}), CObj(New With {.roots = New With {.listChanged = False}, .elicitation = New Dictionary(Of String, Object)})),
                    .clientInfo = New With {.name = "Agentic CLI", .version = AppInfo.Version}}).ConfigureAwait(False)
                Dim negotiated = JsonStr(init, "protocolVersion", Nothing)
                If negotiated IsNot Nothing Then _protocolVersion = negotiated
                Dim caps As JsonElement
                If init.ValueKind = JsonValueKind.Object AndAlso init.TryGetProperty("capabilities", caps) Then Capabilities = caps.Clone()
                Dim info As JsonElement
                If init.ValueKind = JsonValueKind.Object AndAlso init.TryGetProperty("serverInfo", info) Then
                    Status.ServerInfo = Trim(JsonStr(info, "name", "") & " " & JsonStr(info, "version", ""))
                End If
                Await NotifyAsync("notifications/initialized").ConfigureAwait(False)
                Await RefreshToolsAsync().ConfigureAwait(False)
                If HasCapability("prompts") Then
                    Try
                        Prompts = (Await ListAllAsync("prompts/list", "prompts").ConfigureAwait(False)).Select(Function(p)
                            Dim args As New List(Of (Name As String, Required As Boolean))
                            Dim a As JsonElement
                            If p.TryGetProperty("arguments", a) AndAlso a.ValueKind = JsonValueKind.Array Then
                                For Each arg In a.EnumerateArray()
                                    Dim req As JsonElement
                                    args.Add((JsonStr(arg, "name", ""), arg.TryGetProperty("required", req) AndAlso req.ValueKind = JsonValueKind.True))
                                Next
                            End If
                            Return (JsonStr(p, "name", ""), JsonStr(p, "description", ""), args)
                        End Function).Where(Function(p) p.Item1.Length > 0).ToList()
                    Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is InvalidOperationException OrElse TypeOf ex Is TimeoutException
                    End Try
                End If
                Status.Connected = True
                Status.Error = Nothing
                Status.StartedUtc = DateTime.UtcNow
            Catch ex As Exception
                Status.Connected = False
                Status.Error = ex.Message & LastLog() & If(_missing.Count > 0, " (not set: " & String.Join(", ", _missing.Distinct()) & ")", "")
                Shutdown()
            Finally
                Status.Connecting = False
            End Try
        End Function

        Private Function LastLog() As String
            SyncLock Status.RecentLog
                Dim tail = Status.RecentLog.Skip(Math.Max(0, Status.RecentLog.Count - 3)).ToList()
                Return If(tail.Count = 0, "", " — " & String.Join(" | ", tail))
            End SyncLock
        End Function

        Private Sub Log(line As String)
            If String.IsNullOrWhiteSpace(line) Then Return
            SyncLock Status.RecentLog
                Status.RecentLog.Add(If(line.Length > 400, line.Substring(0, 400) & "…", line))
                If Status.RecentLog.Count > 40 Then Status.RecentLog.RemoveAt(0)
            End SyncLock
        End Sub

        Public Async Function RefreshToolsAsync() As Task
            Dim tools As New List(Of McpToolInfo)
            Dim cursor As String = Nothing
            Do
                Dim result = Await RequestAsync("tools/list", If(cursor Is Nothing, CObj(New Dictionary(Of String, Object)), New With {cursor})).ConfigureAwait(False)
                Dim list As JsonElement
                If result.ValueKind = JsonValueKind.Object AndAlso result.TryGetProperty("tools", list) AndAlso list.ValueKind = JsonValueKind.Array Then
                    For Each t In list.EnumerateArray()
                        Dim schema As JsonElement
                        tools.Add(New McpToolInfo With {.Server = Options.Id, .Name = JsonStr(t, "name", ""), .Description = JsonStr(t, "description", ""),
                                                        .InputSchema = If(t.TryGetProperty("inputSchema", schema), schema.GetRawText(), Nothing)})
                    Next
                End If
                cursor = JsonStr(result, "nextCursor", Nothing)
            Loop While cursor IsNot Nothing AndAlso tools.Count < 1000
            Status.Tools = tools.Where(Function(t) t.Name.Length > 0).ToList()
        End Function

        ''' <summary>Every page of a list (resources/list, prompts/list …).</summary>
        Public Async Function ListAllAsync(method As String, key As String) As Task(Of List(Of JsonElement))
            Dim all As New List(Of JsonElement)
            Dim cursor As String = Nothing
            For page = 1 To 20
                Dim result = Await RequestAsync(method, If(cursor Is Nothing, CObj(New Dictionary(Of String, Object)), New With {cursor})).ConfigureAwait(False)
                Dim list As JsonElement
                If result.ValueKind = JsonValueKind.Object AndAlso result.TryGetProperty(key, list) AndAlso list.ValueKind = JsonValueKind.Array Then all.AddRange(list.EnumerateArray().Select(Function(x) x.Clone()))
                cursor = JsonStr(result, "nextCursor", Nothing)
                If cursor Is Nothing Then Exit For
            Next
            Return all
        End Function

        ''' <summary>A resource's text (pictures as pictures).</summary>
        Public Async Function ReadResourceAsync(uri As String) As Task(Of McpToolResult)
            Try
                Dim result = Await RequestAsync("resources/read", New With {uri}).ConfigureAwait(False)
                Dim text As New StringBuilder()
                Dim images As New List(Of (MediaType As String, Data As String))
                Dim contents As JsonElement
                If result.ValueKind = JsonValueKind.Object AndAlso result.TryGetProperty("contents", contents) AndAlso contents.ValueKind = JsonValueKind.Array Then
                    For Each c In contents.EnumerateArray()
                        Dim mime = JsonStr(c, "mimeType", "")
                        If JsonStr(c, "text", Nothing) IsNot Nothing Then
                            text.AppendLine(JsonStr(c, "text", ""))
                        ElseIf mime.StartsWith("image/", StringComparison.Ordinal) AndAlso JsonStr(c, "blob", Nothing) IsNot Nothing Then
                            images.Add((mime, JsonStr(c, "blob", "")))
                            text.AppendLine($"[picture {images.Count}: {JsonStr(c, "uri", uri)}]")
                        Else
                            text.AppendLine($"[binary {mime} resource]")
                        End If
                    Next
                End If
                Return New McpToolResult With {.Ok = True, .Content = If(text.Length = 0, "(empty)", text.ToString().TrimEnd()), .Images = images}
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is InvalidOperationException OrElse TypeOf ex Is TimeoutException
                Return New McpToolResult With {.Ok = False, .IsError = True, .Error = ex.Message}
            End Try
        End Function

        ''' <summary>A prompt with its arguments, as the text of its messages.</summary>
        Public Async Function GetPromptAsync(name As String, args As Dictionary(Of String, String)) As Task(Of String)
            Dim result = Await RequestAsync("prompts/get", New With {name, .arguments = args}).ConfigureAwait(False)
            Dim parts As New List(Of String)
            Dim messages As JsonElement
            If result.ValueKind = JsonValueKind.Object AndAlso result.TryGetProperty("messages", messages) AndAlso messages.ValueKind = JsonValueKind.Array Then
                For Each m In messages.EnumerateArray()
                    Dim c As JsonElement
                    If Not m.TryGetProperty("content", c) Then Continue For
                    Dim body = If(JsonStr(c, "type", "") = "text", JsonStr(c, "text", ""), "[" & JsonStr(c, "type", "") & "]")
                    parts.Add(If(JsonStr(m, "role", "user") = "assistant", "Assistant: ", "") & body)
                Next
            End If
            Return String.Join(vbLf & vbLf, parts)
        End Function

        Public Async Function CallToolAsync(toolName As String, argumentsJson As String) As Task(Of McpToolResult)
            Dim started = Stopwatch.StartNew()
            Dim args As JsonNode = Nothing
            Try
                args = JsonNode.Parse(If(String.IsNullOrWhiteSpace(argumentsJson), "{}", argumentsJson))
            Catch ex As JsonException
            End Try
            If TypeOf args IsNot JsonObject Then args = New JsonObject()
            Try
                Dim result = Await RequestAsync("tools/call", New JsonObject From {{"name", toolName}, {"arguments", args}}).ConfigureAwait(False)
                Status.LastUsedUtc = DateTime.UtcNow
                Status.Calls += 1
                Dim text As New StringBuilder()
                Dim images As New List(Of (MediaType As String, Data As String))
                Dim content As JsonElement
                If result.ValueKind = JsonValueKind.Object AndAlso result.TryGetProperty("content", content) AndAlso content.ValueKind = JsonValueKind.Array Then
                    For Each item In content.EnumerateArray()
                        Select Case JsonStr(item, "type", "text")
                            Case "text" : text.AppendLine(JsonStr(item, "text", ""))
                            Case "image"
                                ' A picture (a browser screenshot) goes to the model as a picture.
                                Dim data = JsonStr(item, "data", "")
                                If data.Length > 0 Then images.Add((JsonStr(item, "mimeType", "image/png"), data))
                                text.AppendLine($"[picture {images.Count}: {JsonStr(item, "mimeType", "image")}]")
                            Case "resource"
                                Dim res As JsonElement
                                If item.TryGetProperty("resource", res) Then text.AppendLine(If(JsonStr(res, "text", Nothing), $"[resource {JsonStr(res, "uri", "")}]"))
                            Case Else : text.AppendLine(item.GetRawText())
                        End Select
                    Next
                End If
                Dim structured As JsonElement
                If text.Length = 0 AndAlso result.ValueKind = JsonValueKind.Object AndAlso result.TryGetProperty("structuredContent", structured) Then text.Append(structured.GetRawText())
                Dim errEl As JsonElement
                Dim isError = result.ValueKind = JsonValueKind.Object AndAlso result.TryGetProperty("isError", errEl) AndAlso errEl.ValueKind = JsonValueKind.True
                Return New McpToolResult With {.Ok = Not isError, .Content = text.ToString().TrimEnd(), .IsError = isError, .DurationMs = started.ElapsedMilliseconds, .Images = images}
            Catch ex As Exception
                Return New McpToolResult With {.Ok = False, .IsError = True, .DurationMs = started.ElapsedMilliseconds, .Error = ex.Message}
            End Try
        End Function

        ' ------------------------------------------------------------------ JSON-RPC

        Public Async Function RequestAsync(method As String, params As Object) As Task(Of JsonElement)
            If _disposed Then Throw New ObjectDisposedException("MCP connection")
            Dim id = Interlocked.Increment(_nextId)
            Dim waiter As New TaskCompletionSource(Of JsonElement)(TaskCreationOptions.RunContinuationsAsynchronously)
            _pending(id) = waiter
            Try
                Dim payload = Jx.Stringify(New With {.jsonrpc = "2.0", .id = id, .method = method, .params = params})
                Await SendAsync(payload, id).ConfigureAwait(False)
                Dim timeout = Task.Delay(TimeSpan.FromSeconds(Math.Max(10, Options.TimeoutSeconds)))
                If Await Task.WhenAny(waiter.Task, timeout).ConfigureAwait(False) IsNot waiter.Task Then
                    Throw New TimeoutException($"{method}: no answer within {Math.Max(10, Options.TimeoutSeconds)} s")
                End If
                Return Await waiter.Task.ConfigureAwait(False)
            Finally
                _pending.TryRemove(id, Nothing)
            End Try
        End Function

        Private Function NotifyAsync(method As String) As Task
            Return SendAsync(Jx.Stringify(New With {.jsonrpc = "2.0", .method = method}), 0)
        End Function

        ''' <summary>An answer, a notification or a request from the server.</summary>
        Private Sub OnMessage(json As String)
            Dim root As JsonElement
            Try
                Using doc = JsonDocument.Parse(json)
                    root = doc.RootElement.Clone()
                End Using
            Catch ex As JsonException
                Log("not JSON: " & json)
                Return
            End Try
            If root.ValueKind = JsonValueKind.Array Then
                For Each item In root.EnumerateArray()
                    OnMessage(item.GetRawText())
                Next
                Return
            End If
            If root.ValueKind <> JsonValueKind.Object Then Return
            Dim idEl As JsonElement, methodEl As JsonElement
            Dim hasId = root.TryGetProperty("id", idEl) AndAlso idEl.ValueKind = JsonValueKind.Number
            Dim hasMethod = root.TryGetProperty("method", methodEl)
            If hasId AndAlso Not hasMethod Then
                Dim waiter As TaskCompletionSource(Of JsonElement) = Nothing
                If _pending.TryGetValue(idEl.GetInt32(), waiter) Then
                    Dim errEl As JsonElement, resultEl As JsonElement
                    If root.TryGetProperty("error", errEl) Then
                        waiter.TrySetException(New InvalidOperationException(JsonStr(errEl, "message", "MCP error")))
                    ElseIf root.TryGetProperty("result", resultEl) Then
                        waiter.TrySetResult(resultEl)
                    Else
                        waiter.TrySetResult(Nothing)
                    End If
                End If
                Return
            End If
            If Not hasMethod Then Return
            Dim method = methodEl.GetString()
            If root.TryGetProperty("id", idEl) Then
                ' A request from the server: ping and roots/list are answered, anything else is politely refused.
                Dim reply As String
                If method = "elicitation/create" Then
                    ' The server asks the user something while a tool runs: the app asks, the answer goes back.
                    Dim reqId = idEl.Clone()
                    Dim params As JsonElement = Nothing
                    root.TryGetProperty("params", params)
                    Dim p = params.Clone()
                    Dim unusedAsk = Task.Run(Async Function()
                                                 Dim answer As JsonObject = Nothing
                                                 Try
                                                     Dim schema As JsonElement = Nothing
                                                     If p.ValueKind = JsonValueKind.Object Then p.TryGetProperty("requestedSchema", schema)
                                                     If Elicit IsNot Nothing Then answer = Await Elicit(Options.Name, JsonStr(p, "message", ""), schema).ConfigureAwait(False)
                                                 Catch ex As Exception
                                                     answer = New JsonObject From {{"action", "cancel"}}
                                                 End Try
                                                 If answer Is Nothing Then answer = New JsonObject From {{"action", "decline"}}
                                                 Dim msg As New JsonObject From {{"jsonrpc", "2.0"}, {"id", JsonNode.Parse(reqId.GetRawText())}, {"result", answer}}
                                                 Await SendAsync(msg.ToJsonString(), 0).ConfigureAwait(False)
                                             End Function)
                    Return
                End If
                Select Case method
                    Case "ping" : reply = Jx.Stringify(New With {.jsonrpc = "2.0", .id = idEl, .result = New Dictionary(Of String, Object)})
                    Case "roots/list" : reply = Jx.Stringify(New With {.jsonrpc = "2.0", .id = idEl, .result = New With {.roots = Roots.Select(Function(r) CObj(New With {.uri = New Uri(r).AbsoluteUri, .name = IO.Path.GetFileName(r.TrimEnd("\"c))})).ToList()}})
                    Case Else : reply = Jx.Stringify(New With {.jsonrpc = "2.0", .id = idEl, .error = New With {.code = -32601, .message = "Not supported by this client."}})
                End Select
                Dim unused = SendAsync(reply, 0)
            ElseIf method = "notifications/tools/list_changed" Then
                Dim unused = Task.Run(Async Function()
                                          Try
                                              Await RefreshToolsAsync().ConfigureAwait(False)
                                              RaiseEvent ToolsChanged(Me, EventArgs.Empty)
                                          Catch
                                          End Try
                                      End Function)
            ElseIf method = "notifications/message" Then
                Dim p As JsonElement
                If root.TryGetProperty("params", p) Then Log(p.GetRawText())
            End If
        End Sub

        Private Async Function SendAsync(payload As String, id As Integer) As Task
            Select Case Options.Transport
                Case McpTransportKind.Stdio
                    If _process Is Nothing OrElse _process.HasExited Then Throw New IOException("the server process is not running" & LastLog())
                    Await _writeLock.WaitAsync().ConfigureAwait(False)
                    Try
                        Await _process.StandardInput.WriteLineAsync(payload).ConfigureAwait(False)
                        Await _process.StandardInput.FlushAsync().ConfigureAwait(False)
                    Finally
                        _writeLock.Release()
                    End Try
                Case McpTransportKind.Sse
                    Using req As New HttpRequestMessage(HttpMethod.Post, _ssePostUrl) With {.Content = New StringContent(payload, Encoding.UTF8, "application/json")}
                        AddBearer(req)
                        Using response = Await _http.SendAsync(req).ConfigureAwait(False)
                            If Not response.IsSuccessStatusCode Then Throw New IOException($"POST {CInt(response.StatusCode)}")
                        End Using
                    End Using
                Case Else
                    Await PostStreamableAsync(payload).ConfigureAwait(False)
            End Select
        End Function

        ' ------------------------------------------------------------------ stdio

        Private Sub StartStdio()
            Dim psi = ResolveStart(X(Options.Command), Options.Args.Select(AddressOf X).ToList())
            psi.UseShellExecute = False
            psi.CreateNoWindow = True
            psi.RedirectStandardInput = True
            psi.RedirectStandardOutput = True
            psi.RedirectStandardError = True
            psi.StandardOutputEncoding = New UTF8Encoding(False)
            psi.StandardErrorEncoding = New UTF8Encoding(False)
            Dim cwd = X(Options.Cwd)
            If Not String.IsNullOrWhiteSpace(cwd) AndAlso Directory.Exists(cwd) Then psi.WorkingDirectory = cwd
            For Each kv In _vars
                psi.Environment(kv.Key) = kv.Value
            Next
            _process = Process.Start(psi)
            If _process Is Nothing Then Throw New IOException($"could not start {Options.Command}")
            Dim proc = _process
            Dim unusedOut = Task.Run(Async Function()
                                         Try
                                             While True
                                                 Dim line = Await proc.StandardOutput.ReadLineAsync().ConfigureAwait(False)
                                                 If line Is Nothing Then Exit While
                                                 line = line.Trim()
                                                 If line.StartsWith("{") OrElse line.StartsWith("[") Then OnMessage(line) Else Log(line)
                                             End While
                                         Catch
                                         End Try
                                         FailPending("the server process ended" & LastLog())
                                     End Function)
            Dim unusedErr = Task.Run(Async Function()
                                         Try
                                             While True
                                                 Dim line = Await proc.StandardError.ReadLineAsync().ConfigureAwait(False)
                                                 If line Is Nothing Then Exit While
                                                 Log(line)
                                             End While
                                         Catch
                                         End Try
                                     End Function)
        End Sub

        ''' <summary>
        ''' On Windows, npx/npm/uvx and friends are .cmd scripts that only start through cmd.exe: the command is looked up
        ''' on PATH with PATHEXT and .cmd/.bat files run via "cmd /d /c".
        ''' </summary>
        Friend Shared Function ResolveStart(command As String, args As List(Of String)) As ProcessStartInfo
            Dim psi As New ProcessStartInfo()
            Dim resolved = FindOnPath(command)
            Dim ext = Path.GetExtension(If(resolved, command)).ToLowerInvariant()
            If OperatingSystem.IsWindows() AndAlso (ext = ".cmd" OrElse ext = ".bat") Then
                psi.FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe")
                psi.ArgumentList.Add("/d")
                psi.ArgumentList.Add("/c")
                psi.ArgumentList.Add(resolved)
            Else
                psi.FileName = If(resolved, command)
            End If
            For Each a In args
                psi.ArgumentList.Add(a)
            Next
            Return psi
        End Function

        Private Shared Function FindOnPath(command As String) As String
            Return Platform.Which(command)
        End Function

        ' ------------------------------------------------------------------ HTTP

        Private Sub StartHttp()
            _http = New HttpClient() With {.Timeout = TimeSpan.FromSeconds(Math.Max(15, Options.TimeoutSeconds + 5))}
            For Each kv In Options.Headers
                Dim v = X(kv.Value).Trim()
                ' A header whose value came out empty (an unset optional token) is left out.
                If v.Length = 0 OrElse v.Equals("Bearer", StringComparison.OrdinalIgnoreCase) Then Continue For
                _http.DefaultRequestHeaders.TryAddWithoutValidation(kv.Key, v)
            Next
        End Sub

        ''' <summary>The OAuth token on a request (when the config gives no Authorization header of its own).</summary>
        Private Sub AddBearer(req As HttpRequestMessage)
            If Not _explicitAuth AndAlso Not String.IsNullOrEmpty(_bearer) Then req.Headers.TryAddWithoutValidation("Authorization", "Bearer " & _bearer)
        End Sub

        ''' <summary>A 401: the sign-in is refreshed once (True: try again); else the server needs the user to sign in.</summary>
        Private Async Function UnauthorizedAsync(response As HttpResponseMessage, retried As Boolean) As Task(Of Boolean)
            WwwAuthenticate = If(response.Headers.WwwAuthenticate.Count > 0, String.Join(", ", response.Headers.WwwAuthenticate.Select(Function(h) h.ToString())), Nothing)
            If Not retried AndAlso Not _explicitAuth Then
                Dim fresh = Await McpOAuth.TokenAsync(_endpoint, forceRefresh:=True).ConfigureAwait(False)
                If Not String.IsNullOrEmpty(fresh) Then
                    _bearer = fresh
                    Return True
                End If
            End If
            Status.NeedsSignIn = Not _explicitAuth
            Throw New IOException(If(_explicitAuth, "the server refused the Authorization header (401)", $"{Options.Name} needs you to sign in: /mcp login {Options.Id}"))
        End Function

        Private Async Function PostStreamableAsync(payload As String, Optional retried As Boolean = False) As Task
            Using req As New HttpRequestMessage(HttpMethod.Post, _endpoint) With {.Content = New StringContent(payload, Encoding.UTF8, "application/json")}
                req.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream")
                If _sessionId IsNot Nothing Then req.Headers.TryAddWithoutValidation("Mcp-Session-Id", _sessionId)
                req.Headers.TryAddWithoutValidation("MCP-Protocol-Version", _protocolVersion)
                AddBearer(req)
                Using response = Await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(False)
                    If response.StatusCode = Net.HttpStatusCode.Unauthorized AndAlso Await UnauthorizedAsync(response, retried).ConfigureAwait(False) Then
                        Await PostStreamableAsync(payload, True).ConfigureAwait(False)
                        Return
                    End If
                    Dim sid As IEnumerable(Of String) = Nothing
                    If response.Headers.TryGetValues("Mcp-Session-Id", sid) Then _sessionId = sid.FirstOrDefault()
                    If Not response.IsSuccessStatusCode Then
                        Dim body = Await response.Content.ReadAsStringAsync().ConfigureAwait(False)
                        Throw New IOException($"{CInt(response.StatusCode)} {response.ReasonPhrase}: {If(body.Length > 300, body.Substring(0, 300), body)}")
                    End If
                    Dim media = If(response.Content.Headers.ContentType?.MediaType, "")
                    If media.Contains("event-stream") Then
                        Using stream = Await response.Content.ReadAsStreamAsync().ConfigureAwait(False), reader As New StreamReader(stream)
                            Await ReadEventsAsync(reader, Nothing).ConfigureAwait(False)
                        End Using
                    Else
                        Dim body = Await response.Content.ReadAsStringAsync().ConfigureAwait(False)
                        If Not String.IsNullOrWhiteSpace(body) Then OnMessage(body)
                    End If
                End Using
            End Using
        End Function

        ''' <summary>Reads "event:/data:" frames; with onEndpoint it also reports the legacy transport's POST address.</summary>
        Private Async Function ReadEventsAsync(reader As StreamReader, onEndpoint As Action(Of String)) As Task
            Dim evt As String = Nothing
            Dim data As New StringBuilder()
            While True
                Dim line = Await reader.ReadLineAsync().ConfigureAwait(False)
                If line Is Nothing Then Exit While
                If line.Length = 0 Then
                    If data.Length > 0 Then
                        If evt = "endpoint" AndAlso onEndpoint IsNot Nothing Then onEndpoint(data.ToString().Trim()) Else OnMessage(data.ToString())
                    End If
                    evt = Nothing
                    data.Clear()
                ElseIf line.StartsWith("event:") Then
                    evt = line.Substring(6).Trim()
                ElseIf line.StartsWith("data:") Then
                    If data.Length > 0 Then data.Append(vbLf)
                    data.Append(line.Substring(5).TrimStart())
                End If
            End While
        End Function

        Private Async Function StartSseAsync(Optional retried As Boolean = False) As Task
            If _http Is Nothing Then StartHttp()
            _http.Timeout = Timeout.InfiniteTimeSpan
            _sseCts = New CancellationTokenSource()
            Dim endpointKnown As New TaskCompletionSource(Of String)(TaskCreationOptions.RunContinuationsAsynchronously)
            Dim req As New HttpRequestMessage(HttpMethod.Get, _endpoint)
            req.Headers.TryAddWithoutValidation("Accept", "text/event-stream")
            AddBearer(req)
            Dim sseResponse = Await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, _sseCts.Token).ConfigureAwait(False)
            If sseResponse.StatusCode = Net.HttpStatusCode.Unauthorized Then
                Dim again = Await UnauthorizedAsync(sseResponse, retried).ConfigureAwait(False)
                sseResponse.Dispose()
                If again Then
                    Await StartSseAsync(True).ConfigureAwait(False)
                    Return
                End If
            End If
            sseResponse.EnsureSuccessStatusCode()
            Dim stream = Await sseResponse.Content.ReadAsStreamAsync().ConfigureAwait(False)
            Dim unused = Task.Run(Async Function()
                                      Try
                                          Using sseResponse
                                              Using reader As New StreamReader(stream)
                                                  Await ReadEventsAsync(reader, Sub(u) endpointKnown.TrySetResult(u)).ConfigureAwait(False)
                                              End Using
                                          End Using
                                      Catch
                                      End Try
                                      endpointKnown.TrySetException(New IOException("the event stream closed"))
                                      FailPending("the event stream closed")
                                  End Function)
            If Await Task.WhenAny(endpointKnown.Task, Task.Delay(TimeSpan.FromSeconds(15))).ConfigureAwait(False) IsNot endpointKnown.Task Then
                Throw New TimeoutException("the server did not send its endpoint")
            End If
            _ssePostUrl = New Uri(New Uri(_endpoint), Await endpointKnown.Task.ConfigureAwait(False)).AbsoluteUri
        End Function

        ' ------------------------------------------------------------------ shutdown

        Private Sub FailPending(reason As String)
            For Each kv In _pending
                kv.Value.TrySetException(New IOException(reason))
            Next
            If Not _disposed Then
                Status.Connected = False
                If Status.Error Is Nothing Then Status.Error = reason
            End If
        End Sub

        Private Sub Shutdown()
            Try
                _sseCts?.Cancel()
            Catch
            End Try
            If _process IsNot Nothing Then
                Try
                    If Not _process.HasExited Then _process.Kill(True)
                Catch
                End Try
                _process.Dispose()
                _process = Nothing
            End If
            If _http IsNot Nothing AndAlso Options.Transport <> McpTransportKind.Stdio AndAlso _sessionId IsNot Nothing Then
                ' Streamable HTTP: end the session politely (best effort).
                Try
                    Using req As New HttpRequestMessage(HttpMethod.Delete, _endpoint)
                        req.Headers.TryAddWithoutValidation("Mcp-Session-Id", _sessionId)
                        _http.SendAsync(req).Wait(2000)
                    End Using
                Catch
                End Try
            End If
            _http?.Dispose()
            _http = Nothing
        End Sub

        Public Sub Dispose() Implements IDisposable.Dispose
            If _disposed Then Return
            _disposed = True
            Status.Connected = False
            FailPending("disconnected")
            Shutdown()
        End Sub

        Friend Shared Function JsonStr(e As JsonElement, name As String, fallback As String) As String
            Dim el As JsonElement
            If e.ValueKind = JsonValueKind.Object AndAlso e.TryGetProperty(name, el) AndAlso el.ValueKind = JsonValueKind.String Then Return el.GetString()
            Return fallback
        End Function

    End Class

End Namespace
