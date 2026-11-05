Option Strict On
Option Explicit On
Option Infer On

Imports System.Net
Imports System.Net.Http
Imports System.Text
Imports System.Text.Json
Imports System.Text.Json.Nodes
Imports System.Text.RegularExpressions
Imports AgenticCli.Agent
Imports AgenticCli.Ui

Namespace Tools

    ''' <summary>WebFetch: gets a page and turns its HTML into readable Markdown-like text (cached for 15 minutes).</summary>
    Friend NotInheritable Class WebFetchTool
        Implements ITool

        Private Shared ReadOnly Http As New HttpClient(New SocketsHttpHandler With {.AllowAutoRedirect = True, .MaxAutomaticRedirections = 8,
                                                                                    .AutomaticDecompression = DecompressionMethods.All}) With {.Timeout = TimeSpan.FromSeconds(30)}
        Private Shared ReadOnly Cache As New Dictionary(Of String, (Text As String, At As DateTime))

        Shared Sub New()
            Http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AgenticCLI/" & Core.AppInfo.Version)
            Http.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/json,text/plain;q=0.9,*/*;q=0.8")
        End Sub

        Public ReadOnly Property Name As String = "WebFetch" Implements ITool.Name
        Public ReadOnly Property Description As String = "Fetches a URL (http/https) and returns its content as Markdown-like text (HTML converted; JSON and text as they are). " &
            "prompt says what you want from the page; read the result with it in mind. Results are cached for 15 minutes. " &
            "If the server redirects to another host you are told the new URL; fetch that one. Prefer an MCP fetch tool when one is available." Implements ITool.Description
        Public ReadOnly Property IsReadOnly As Boolean = True Implements ITool.IsReadOnly

        Public Function Parameters() As JsonObject Implements ITool.Parameters
            Return ToolKit.Schema({"url"}, ("url", "string", "The URL to fetch"), ("prompt", "string", "What to look for in the page"))
        End Function

        Public Function Title(input As JsonElement, state As AgentState) As String Implements ITool.Title
            Return "Fetch(" & ToolKit.OneLine(ToolKit.Arg(input, "url", ""), 100) & ")"
        End Function

        Public Function Preview(input As JsonElement, state As AgentState) As String Implements ITool.Preview
            Dim p = ToolKit.Arg(input, "prompt")
            Return If(p Is Nothing, Nothing, "      " & Ansi.Muted(ToolKit.OneLine(p, 200)) & Environment.NewLine)
        End Function

        Public Async Function RunAsync(input As JsonElement, ctx As ToolContext) As Task(Of ToolResult) Implements ITool.RunAsync
            Dim url = ToolKit.Arg(input, "url", "").Trim()
            Dim uri As Uri = Nothing
            If Not Uri.TryCreate(url, UriKind.Absolute, uri) OrElse (uri.Scheme <> "http" AndAlso uri.Scheme <> "https") Then Return ToolResult.Fail("Give a full http(s) URL.")
            Dim prompt = ToolKit.Arg(input, "prompt")
            SyncLock Cache
                Dim hit As (Text As String, At As DateTime) = Nothing
                If Cache.TryGetValue(url, hit) AndAlso (DateTime.UtcNow - hit.At).TotalMinutes < 15 Then Return Finish(url, hit.Text, prompt, True)
            End SyncLock
            Try
                Using resp = Await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ctx.Cancel).ConfigureAwait(False)
                    Dim final = resp.RequestMessage?.RequestUri
                    If final IsNot Nothing AndAlso Not final.Host.Equals(uri.Host, StringComparison.OrdinalIgnoreCase) AndAlso
                       Not final.Host.EndsWith("." & uri.Host, StringComparison.OrdinalIgnoreCase) AndAlso Not uri.Host.EndsWith("." & final.Host, StringComparison.OrdinalIgnoreCase) Then
                        Return ToolResult.Done($"The URL redirected to a different host: {final.AbsoluteUri}. Fetch that URL if you want its content.", "Redirected to " & final.Host)
                    End If
                    If Not resp.IsSuccessStatusCode Then Return ToolResult.Fail($"HTTP {CInt(resp.StatusCode)} {resp.ReasonPhrase} for {url}", $"HTTP {CInt(resp.StatusCode)}")
                    Dim media = If(resp.Content.Headers.ContentType?.MediaType, "").ToLowerInvariant()
                    If media.StartsWith("image/") OrElse media = "application/pdf" OrElse media.StartsWith("audio/") OrElse media.StartsWith("video/") OrElse media = "application/octet-stream" Then
                        Return ToolResult.Done($"{url} is {media} ({resp.Content.Headers.ContentLength.GetValueOrDefault():N0} bytes), not text.", media)
                    End If
                    Dim bytes = Await ReadLimitedAsync(resp, 10 * 1024 * 1024, ctx).ConfigureAwait(False)
                    Dim charset = resp.Content.Headers.ContentType?.CharSet
                    Dim enc As Encoding = Encoding.UTF8
                    Try
                        If Not String.IsNullOrWhiteSpace(charset) Then enc = Encoding.GetEncoding(charset.Trim(""""c))
                    Catch ex As ArgumentException
                    End Try
                    Dim raw = enc.GetString(bytes)
                    Dim text = If(media.Contains("html") OrElse (media.Length = 0 AndAlso raw.TrimStart().StartsWith("<")), HtmlToText(raw, If(final, uri)), raw)
                    SyncLock Cache
                        Cache(url) = (text, DateTime.UtcNow)
                    End SyncLock
                    Return Finish(url, text, prompt, False)
                End Using
            Catch ex As TaskCanceledException When Not ctx.Cancel.IsCancellationRequested
                Return ToolResult.Fail("The site did not answer within 30 seconds.", "Timed out")
            Catch ex As HttpRequestException
                Return ToolResult.Fail("Fetch failed: " & ex.Message, "Fetch failed")
            End Try
        End Function

        Private Shared Async Function ReadLimitedAsync(resp As HttpResponseMessage, max As Integer, ctx As ToolContext) As Task(Of Byte())
            Using s = Await resp.Content.ReadAsStreamAsync(ctx.Cancel).ConfigureAwait(False)
                Using ms As New IO.MemoryStream()
                    Dim buf(81919) As Byte
                    While ms.Length < max
                        Dim n = Await s.ReadAsync(buf, 0, buf.Length, ctx.Cancel).ConfigureAwait(False)
                        If n <= 0 Then Exit While
                        ms.Write(buf, 0, n)
                    End While
                    Return ms.ToArray()
                End Using
            End Using
        End Function

        Private Shared Function Finish(url As String, text As String, prompt As String, cached As Boolean) As ToolResult
            Dim body = ToolKit.Clip(text.Trim(), 60000)
            Dim content = $"Content of {url}:" & vbLf & vbLf & body & If(prompt Is Nothing, "", vbLf & vbLf & $"(Answer from the content above: {prompt})")
            Dim kb = Encoding.UTF8.GetByteCount(text) / 1024.0
            Return ToolResult.Done(content, $"Received {kb:N1} KB" & If(cached, Ansi.Muted(" (cached)"), ""))
        End Function

        ''' <summary>HTML to Markdown-like text: headings, links, lists, code, paragraphs; scripts and chrome removed.</summary>
        Friend Shared Function HtmlToText(html As String, baseUri As Uri) As String
            Dim s = html
            Dim titleMatch = Regex.Match(s, "<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase Or RegexOptions.Singleline)
            Dim title = If(titleMatch.Success, WebUtility.HtmlDecode(titleMatch.Groups(1).Value).Trim(), Nothing)
            s = Regex.Replace(s, "<(script|style|noscript|svg|head|iframe|template|form)\b.*?</\1\s*>", "", RegexOptions.IgnoreCase Or RegexOptions.Singleline)
            s = Regex.Replace(s, "<!--.*?-->", "", RegexOptions.Singleline)
            ' Prefer the main content when the page marks it.
            Dim main = Regex.Match(s, "<(main|article)\b[^>]*>(.*?)</\1>", RegexOptions.IgnoreCase Or RegexOptions.Singleline)
            If main.Success AndAlso main.Groups(2).Value.Length > 500 Then s = main.Groups(2).Value
            s = Regex.Replace(s, "<(nav|footer|aside)\b.*?</\1\s*>", "", RegexOptions.IgnoreCase Or RegexOptions.Singleline)
            s = Regex.Replace(s, "<pre[^>]*>(.*?)</pre>", Function(m) vbLf & "```" & vbLf & WebUtility.HtmlDecode(Regex.Replace(m.Groups(1).Value, "<[^>]+>", "")) & vbLf & "```" & vbLf, RegexOptions.IgnoreCase Or RegexOptions.Singleline)
            s = Regex.Replace(s, "<h([1-6])[^>]*>(.*?)</h\1>", Function(m) vbLf & vbLf & New String("#"c, Integer.Parse(m.Groups(1).Value)) & " " & Regex.Replace(m.Groups(2).Value, "<[^>]+>", "").Trim() & vbLf, RegexOptions.IgnoreCase Or RegexOptions.Singleline)
            s = Regex.Replace(s, "<a\b[^>]*href\s*=\s*[""']([^""']+)[""'][^>]*>(.*?)</a>",
                              Function(m)
                                  Dim label = Regex.Replace(m.Groups(2).Value, "<[^>]+>", "").Trim()
                                  If label.Length = 0 Then Return ""
                                  Dim href = WebUtility.HtmlDecode(m.Groups(1).Value)
                                  Dim abs As Uri = Nothing
                                  If href.StartsWith("#") OrElse href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) Then Return label
                                  If Uri.TryCreate(baseUri, href, abs) Then href = abs.AbsoluteUri
                                  Return $"[{label}]({href})"
                              End Function, RegexOptions.IgnoreCase Or RegexOptions.Singleline)
            s = Regex.Replace(s, "<code[^>]*>(.*?)</code>", Function(m) "`" & Regex.Replace(m.Groups(1).Value, "<[^>]+>", "") & "`", RegexOptions.IgnoreCase Or RegexOptions.Singleline)
            s = Regex.Replace(s, "<(strong|b)\b[^>]*>(.*?)</\1>", "**$2**", RegexOptions.IgnoreCase Or RegexOptions.Singleline)
            s = Regex.Replace(s, "<li[^>]*>", vbLf & "- ", RegexOptions.IgnoreCase)
            s = Regex.Replace(s, "<(br|hr)\s*/?>", vbLf, RegexOptions.IgnoreCase)
            s = Regex.Replace(s, "</(p|div|li|tr|ul|ol|table|section|blockquote|dd|dt)>", vbLf, RegexOptions.IgnoreCase)
            s = Regex.Replace(s, "<(td|th)[^>]*>", " | ", RegexOptions.IgnoreCase)
            s = Regex.Replace(s, "<[^>]+>", "")
            s = WebUtility.HtmlDecode(s)
            s = Regex.Replace(s, "[ \t ]+", " ")
            s = Regex.Replace(s, " *\n *", vbLf)
            s = Regex.Replace(s, "\n{3,}", vbLf & vbLf)
            Return If(title Is Nothing, "", "# " & title & vbLf & vbLf) & s.Trim()
        End Function

    End Class

End Namespace
