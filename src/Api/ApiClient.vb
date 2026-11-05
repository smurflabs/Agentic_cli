Option Strict On
Option Explicit On
Option Infer On

Imports System.IO
Imports System.Net.Http
Imports System.Net.Http.Headers
Imports System.Text
Imports System.Text.Json
Imports System.Threading
Imports AgenticCli.Core

Namespace Api

    ''' <summary>An API failure: HTTP status, the server's error code and message.</summary>
    Friend Class ApiException
        Inherits Exception

        Public ReadOnly Property Status As Integer
        Public ReadOnly Property Code As String

        Public Sub New(status As Integer, code As String, message As String)
            MyBase.New(message)
            Me.Status = status
            Me.Code = code
        End Sub

        Public ReadOnly Property IsAuth As Boolean
            Get
                Return Status = 401 OrElse Status = 403
            End Get
        End Property

        Public ReadOnly Property IsNotFound As Boolean
            Get
                Return Status = 404
            End Get
        End Property

    End Class

    ''' <summary>HTTP client for the Agentic server. Authenticates with the personal API key ("Authorization: Bearer agk_…").</summary>
    Friend NotInheritable Class ApiClient
        Implements IDisposable

        Private ReadOnly _http As HttpClient
        Public ReadOnly Property BaseUrl As String

        Public Sub New(serverUrl As String, apiKey As String)
            BaseUrl = serverUrl.TrimEnd("/"c)
            _http = New HttpClient(New SocketsHttpHandler With {.PooledConnectionLifetime = TimeSpan.FromMinutes(5), .AutomaticDecompression = Net.DecompressionMethods.All}) With {
                .Timeout = Timeout.InfiniteTimeSpan}
            _http.DefaultRequestHeaders.Authorization = New AuthenticationHeaderValue("Bearer", apiKey)
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("agentic-cli/" & AppInfo.Version)
            _http.DefaultRequestHeaders.Accept.ParseAdd("application/json")
        End Sub

        Private Shared Function Timed(ct As CancellationToken, seconds As Integer) As CancellationTokenSource
            Dim cts = CancellationTokenSource.CreateLinkedTokenSource(ct)
            cts.CancelAfter(TimeSpan.FromSeconds(seconds))
            Return cts
        End Function

        Public Async Function SendAsync(method As String, path As String, body As String, ct As CancellationToken, Optional timeoutSeconds As Integer = 120) As Task(Of JsonElement)
            Using cts = Timed(ct, timeoutSeconds)
                Using req As New HttpRequestMessage(New HttpMethod(method), BaseUrl & path)
                    If body IsNot Nothing Then req.Content = New StringContent(body, Encoding.UTF8, "application/json")
                    Try
                        Using resp = Await _http.SendAsync(req, cts.Token).ConfigureAwait(False)
                            Dim text = Await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(False)
                            If Not resp.IsSuccessStatusCode Then Throw ErrorFrom(CInt(resp.StatusCode), text)
                            Return If(String.IsNullOrWhiteSpace(text), Jx.Parse("{}"), Jx.Parse(text))
                        End Using
                    Catch ex As TaskCanceledException When Not ct.IsCancellationRequested
                        Throw New ApiException(0, "TIMEOUT", $"The server did not answer within {timeoutSeconds} s ({BaseUrl}).")
                    Catch ex As HttpRequestException
                        Throw New ApiException(0, "UNREACHABLE", $"Cannot reach {BaseUrl}: {ex.Message}")
                    End Try
                End Using
            End Using
        End Function

        Public Function GetAsync(path As String, ct As CancellationToken, Optional timeoutSeconds As Integer = 60) As Task(Of JsonElement)
            Return SendAsync("GET", path, Nothing, ct, timeoutSeconds)
        End Function

        Public Function PostAsync(path As String, body As String, ct As CancellationToken, Optional timeoutSeconds As Integer = 120) As Task(Of JsonElement)
            Return SendAsync("POST", path, If(body, "{}"), ct, timeoutSeconds)
        End Function

        Public Function PatchAsync(path As String, body As String, ct As CancellationToken) As Task(Of JsonElement)
            Return SendAsync("PATCH", path, body, ct)
        End Function

        Public Function DeleteAsync(path As String, ct As CancellationToken) As Task(Of JsonElement)
            Return SendAsync("DELETE", path, Nothing, ct)
        End Function

        ''' <summary>Uploads raw bytes (POST, application/octet-stream).</summary>
        Public Async Function PostBytesAsync(path As String, bytes As Byte(), ct As CancellationToken) As Task(Of JsonElement)
            Using cts = Timed(ct, 300)
                Using req As New HttpRequestMessage(HttpMethod.Post, BaseUrl & path)
                    req.Content = New ByteArrayContent(bytes)
                    req.Content.Headers.ContentType = New MediaTypeHeaderValue("application/octet-stream")
                    Using resp = Await _http.SendAsync(req, cts.Token).ConfigureAwait(False)
                        Dim text = Await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(False)
                        If Not resp.IsSuccessStatusCode Then Throw ErrorFrom(CInt(resp.StatusCode), text)
                        Return Jx.Parse(text)
                    End Using
                End Using
            End Using
        End Function

        ''' <summary>Downloads a file (GET) to disk and returns its size and SHA-256 (lowercase hex).</summary>
        Public Async Function DownloadAsync(path As String, file As String, ct As CancellationToken, Optional progress As Action(Of Long, Long) = Nothing) As Task(Of (Size As Long, Sha256 As String))
            Using req As New HttpRequestMessage(HttpMethod.Get, BaseUrl & path)
                req.Headers.Accept.Clear()
                req.Headers.Accept.ParseAdd("application/octet-stream")
                Dim resp As HttpResponseMessage
                Try
                    resp = Await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(False)
                Catch ex As HttpRequestException
                    Throw New ApiException(0, "UNREACHABLE", $"Cannot reach {BaseUrl}: {ex.Message}")
                End Try
                Using resp
                    If Not resp.IsSuccessStatusCode Then Throw ErrorFrom(CInt(resp.StatusCode), Await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(False))
                    Dim total = If(resp.Content.Headers.ContentLength, -1L)
                    Using source = Await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(False),
                          target As New FileStream(file, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync:=True),
                          hash = Security.Cryptography.IncrementalHash.CreateHash(Security.Cryptography.HashAlgorithmName.SHA256)
                        Dim buffer(81919) As Byte
                        Dim done = 0L
                        While True
                            Dim n = Await source.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(False)
                            If n = 0 Then Exit While
                            Await target.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(False)
                            hash.AppendData(buffer, 0, n)
                            done += n
                            progress?.Invoke(done, total)
                        End While
                        Return (done, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant())
                    End Using
                End Using
            End Using
        End Function

        ''' <summary>Opens a streaming POST (server-sent events, or accept: e.g. speech as it is made). The caller disposes the response.</summary>
        Public Async Function OpenStreamAsync(path As String, body As String, ct As CancellationToken, Optional accept As String = "text/event-stream") As Task(Of HttpResponseMessage)
            ' No body: a GET (reconnecting to an answer's stream).
            Dim req = If(body Is Nothing, New HttpRequestMessage(HttpMethod.Get, BaseUrl & path),
                         New HttpRequestMessage(HttpMethod.Post, BaseUrl & path) With {.Content = New StringContent(body, Encoding.UTF8, "application/json")})
            req.Headers.Accept.Clear()
            req.Headers.Accept.ParseAdd(accept)
            Dim resp As HttpResponseMessage
            Try
                resp = Await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(False)
            Catch ex As HttpRequestException
                Throw New ApiException(0, "UNREACHABLE", $"Cannot reach {BaseUrl}: {ex.Message}")
            End Try
            If Not resp.IsSuccessStatusCode Then
                Dim text = Await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(False)
                Dim status = CInt(resp.StatusCode)
                resp.Dispose()
                Throw ErrorFrom(status, text)
            End If
            Return resp
        End Function

        Friend Shared Function ErrorFrom(status As Integer, body As String) As ApiException
            Dim code = "HTTP_" & status
            Dim message = If(String.IsNullOrWhiteSpace(body), $"HTTP {status}", body.Trim())
            Dim root As JsonElement
            If Jx.TryParse(body, root) AndAlso root.ValueKind = JsonValueKind.Object Then
                Dim err = Jx.Prop(root, "error")
                If err.ValueKind = JsonValueKind.Object Then
                    code = Jx.StrOf(err, "code", code)
                    message = Jx.StrOf(err, "message", message)
                ElseIf err.ValueKind = JsonValueKind.String Then
                    message = err.GetString()
                ElseIf Jx.Has(root, "message") Then
                    message = Jx.StrOf(root, "message")
                End If
            End If
            If status = 401 Then message &= " — check the API key (agentic login)."
            Return New ApiException(status, code, If(message.Length > 600, message.Substring(0, 600) & "…", message))
        End Function

        Public Sub Dispose() Implements IDisposable.Dispose
            _http.Dispose()
        End Sub

    End Class

    ''' <summary>One server-sent event.</summary>
    Friend Structure SseEvent
        Public Property Name As String
        Public Property Data As String

        Public Function Json() As JsonElement
            Dim e As JsonElement
            If Jx.TryParse(Data, e) Then Return e
            Return Jx.Parse("{}")
        End Function
    End Structure

    ''' <summary>Reads "event: x / data: {...}" frames. Comments (": ping") are skipped.</summary>
    Friend NotInheritable Class SseReader

        Private ReadOnly _reader As StreamReader

        Public Sub New(stream As Stream)
            _reader = New StreamReader(stream, New UTF8Encoding(False))
        End Sub

        Public Async Function NextAsync(ct As CancellationToken) As Task(Of SseEvent?)
            Dim name As String = Nothing
            Dim data As New StringBuilder()
            While True
                Dim line = Await _reader.ReadLineAsync(ct).ConfigureAwait(False)
                If line Is Nothing Then Exit While
                If line.Length = 0 Then
                    If name IsNot Nothing OrElse data.Length > 0 Then Exit While
                    Continue While
                End If
                If line.StartsWith(":", StringComparison.Ordinal) Then Continue While
                If line.StartsWith("event:", StringComparison.Ordinal) Then
                    name = line.Substring(6).Trim()
                ElseIf line.StartsWith("data:", StringComparison.Ordinal) Then
                    If data.Length > 0 Then data.Append(ChrW(10))
                    data.Append(line.Substring(5).TrimStart(" "c))
                End If
            End While
            If name Is Nothing AndAlso data.Length = 0 Then Return Nothing
            Return New SseEvent With {.Name = If(name, "message"), .Data = data.ToString()}
        End Function

    End Class

End Namespace
