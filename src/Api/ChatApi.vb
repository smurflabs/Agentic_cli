Option Strict On
Option Explicit On
Option Infer On

Imports System.Net.Http
Imports System.Text.Json
Imports System.Threading
Imports AgenticCli.Core

Namespace Api

    Friend NotInheritable Class ModelInfo
        Public Property Id As String
        Public Property Name As String
        Public Property Description As String
        Public Property Custom As Boolean
        Public Property OwnedBy As String
        Public Property Routes As New List(Of String)
    End Class

    Friend NotInheritable Class ServerMcpInfo
        Public Property Id As String
        Public Property Name As String
        Public Property Description As String
        Public Property Tools As Integer
        Public Property Connected As Boolean
    End Class

    ''' <summary>What GET /api/chat/meta advertises: models, what the account may use, defaults.</summary>
    Friend NotInheritable Class ChatMeta
        Public Property CustomModels As New List(Of ModelInfo)
        Public Property ProviderModels As New List(Of ModelInfo)
        Public Property ProviderError As String
        Public Property DefaultModel As String
        Public Property WebSearch As Boolean
        Public Property CanTools As Boolean
        Public Property IsAdmin As Boolean
        Public Property CodeInterpreter As Boolean
        Public Property Servers As New List(Of ServerMcpInfo)
        Public Property ContextWindows As New Dictionary(Of String, Integer)(StringComparer.OrdinalIgnoreCase)
        Public Property AutoCompactPercent As Integer = 80
        ''' <summary>The model this account last chatted with (in any client), when it is still offered.</summary>
        Public Property LastModel As String

        Public Function AllModels() As List(Of ModelInfo)
            Return CustomModels.Concat(ProviderModels).ToList()
        End Function

        Public Function Find(id As String) As ModelInfo
            If String.IsNullOrWhiteSpace(id) Then Return Nothing
            Dim all = AllModels()
            Return If(all.FirstOrDefault(Function(m) m.Id.Equals(id, StringComparison.OrdinalIgnoreCase)),
                      all.FirstOrDefault(Function(m) m.Name IsNot Nothing AndAlso m.Name.Equals(id, StringComparison.OrdinalIgnoreCase)))
        End Function

        Public Function WindowFor(model As String) As Integer
            Dim w As Integer
            If model IsNot Nothing AndAlso ContextWindows.TryGetValue(model, w) Then Return w
            Return 128000
        End Function
    End Class

    Friend NotInheritable Class ChatSummary
        Public Property Id As String
        Public Property Title As String
        Public Property Model As String
        Public Property Pinned As Boolean
        Public Property Bookmarked As Boolean
        Public Property Archived As Boolean
        Public Property Live As Boolean
        Public Property UpdatedUtc As DateTime?
        Public Property Preview As String
    End Class

    Friend NotInheritable Class ChatMessage
        Public Property Id As String
        Public Property Role As String
        Public Property Content As String
        Public Property Model As String
        Public Property RoutedModel As String
        Public Property CreatedUtc As DateTime?
        Public Property PromptTokens As Integer
        Public Property CompletionTokens As Integer
        Public Property DurationMs As Long
        Public Property [Error] As String
        Public Property ToolCalls As New List(Of JsonElement)
    End Class

    Friend NotInheritable Class Conversation
        Public Property Id As String
        Public Property Title As String
        Public Property Model As String
        Public Property Settings As JsonElement
        Public Property Messages As New List(Of ChatMessage)
        Public Property Summary As String
    End Class

    Friend NotInheritable Class ContextInfo
        Public Property Used As Integer
        Public Property Max As Integer
        Public Property Percent As Double
        Public Property Model As String
        Public Property Compacted As Boolean
        Public Property Messages As Integer
        Public Property MessagesInFull As Integer
        Public Property AutoCompactPercent As Integer = 80

        Public Shared Function From(e As JsonElement) As ContextInfo
            If e.ValueKind <> JsonValueKind.Object Then Return Nothing
            Return New ContextInfo With {
                .Used = Jx.IntOf(e, "used"), .Max = Jx.IntOf(e, "max"), .Percent = Jx.DblOf(e, "percent"), .Model = Jx.StrOf(e, "model"),
                .Compacted = Jx.BoolOf(e, "compacted"), .Messages = Jx.IntOf(e, "messages"), .MessagesInFull = Jx.IntOf(e, "messagesInFull"),
                .AutoCompactPercent = Jx.IntOf(e, "autoCompactPercent", 80)}
        End Function
    End Class

    ''' <summary>Before/after figures for a compaction, for the one-line note dropped in the conversation.</summary>
    Friend NotInheritable Class CompactStats
        Public Property BeforeTokens As Integer
        Public Property AfterTokens As Integer
        Public Property BeforeMessages As Integer
        Public Property AfterMessages As Integer

        Public Shared Function From(e As JsonElement) As CompactStats
            If e.ValueKind <> JsonValueKind.Object Then Return Nothing
            Return New CompactStats With {
                .BeforeTokens = Jx.IntOf(e, "beforeTokens"), .AfterTokens = Jx.IntOf(e, "afterTokens"),
                .BeforeMessages = Jx.IntOf(e, "beforeMessages"), .AfterMessages = Jx.IntOf(e, "afterMessages")}
        End Function

        ''' <summary>e.g. "Context compacted · 142.1k → 27.3k tokens · 109 → 32 messages".</summary>
        Public Function Line() As String
            Dim k = Function(n As Integer) (Math.Max(0, n) / 1000.0).ToString("0.0", Globalization.CultureInfo.InvariantCulture) & "k"
            Return $"Context compacted · {k(BeforeTokens)} → {k(AfterTokens)} tokens · {BeforeMessages} → {AfterMessages} messages"
        End Function
    End Class

    ''' <summary>The server-side settings of a conversation, sent with every message.</summary>
    Friend NotInheritable Class ServerSettings
        Public Property WebSearch As Boolean
        Public Property CodeInterpreter As Boolean
        Public Property McpServers As New List(Of String)
        Public Property ReasoningEffort As String = "medium"
        ''' <summary>The server's approval policy (auto = ask only when something looks unsafe); the CLI applies its own on top.</summary>
        Public Property PermissionMode As String = "auto"
        ''' <summary>/goal: the server keeps working toward this until it reports the goal met, and retries model failures. Nothing = no goal.</summary>
        Public Property Goal As String
        ''' <summary>/thinking off: the model answers right away, without reasoning (Nothing or True = thinking on).</summary>
        Public Property Thinking As Boolean?
        ''' <summary>Caveman mode: Nothing (off), full, ultra or wenyan (/cavemode).</summary>
        Public Property Caveman As String
        ''' <summary>Max tool-call rounds for the chat (Nothing = server default, 0 = tools off).</summary>
        Public Property MaxToolRounds As Integer?
        ''' <summary>Max web searches per answer (Nothing = server default).</summary>
        Public Property MaxWebSearches As Integer?
        ''' <summary>Parallel agents (Nothing = on; False = one step at a time): the server tells the model to split work and runs a round's subagents together.</summary>
        Public Property Parallel As Boolean?
        ''' <summary>The most subagents at once (Nothing = the server default).</summary>
        Public Property MaxParallelAgents As Integer?
        ''' <summary>Research notes (Nothing = on; False = off): the server saves web research findings to research/&lt;chat&gt;.md here.</summary>
        Public Property ResearchNotes As Boolean?

        Public Function ToJson() As String
            Return Jx.Stringify(New With {.webSearch = WebSearch, .codeInterpreter = CodeInterpreter, .mcpServers = McpServers,
                                          .reasoningEffort = ReasoningEffort, .permissionMode = PermissionMode, .goal = Goal,
                                          .thinking = If(Thinking.HasValue AndAlso Not Thinking.Value, CType(False, Boolean?), Nothing), .caveman = Caveman,
                                          .maxToolRounds = MaxToolRounds, .maxWebSearches = MaxWebSearches,
                                          .parallel = Parallel, .maxParallelAgents = MaxParallelAgents, .researchNotes = ResearchNotes})
        End Function
    End Class

    ''' <summary>Typed calls to the chat endpoints.</summary>
    Friend NotInheritable Class ChatApi

        Private ReadOnly _api As ApiClient

        Public Sub New(api As ApiClient)
            _api = api
        End Sub

        Public ReadOnly Property Client As ApiClient
            Get
                Return _api
            End Get
        End Property

        Private Shared Function Enc(s As String) As String
            Return Uri.EscapeDataString(s)
        End Function

        Public Async Function MetaAsync(ct As CancellationToken) As Task(Of ChatMeta)
            Dim j = Await _api.GetAsync("/api/chat/meta", ct, 90).ConfigureAwait(False)
            Dim m As New ChatMeta With {
                .DefaultModel = Jx.StrOf(j, "defaultModel", Nothing), .WebSearch = Jx.BoolOf(j, "webSearch"), .CanTools = Jx.BoolOf(j, "canTools"),
                .IsAdmin = Jx.BoolOf(j, "isAdmin"), .CodeInterpreter = Jx.BoolOf(j, "codeInterpreter"), .ProviderError = Jx.StrOf(j, "providerError", Nothing),
                .AutoCompactPercent = Jx.IntOf(j, "autoCompactPercent", 80), .LastModel = Jx.StrOf(j, "lastModel", Nothing)}
            For Each c In Jx.ArrOf(j, "customModels")
                m.CustomModels.Add(New ModelInfo With {.Id = Jx.StrOf(c, "id"), .Name = Jx.StrOf(c, "name"), .Description = Jx.StrOf(c, "description"),
                                                       .Custom = True, .Routes = Jx.StringsOf(c, "routes")})
            Next
            For Each c In Jx.ArrOf(j, "providerModels")
                m.ProviderModels.Add(New ModelInfo With {.Id = Jx.StrOf(c, "id"), .Name = Jx.StrOf(c, "name"), .OwnedBy = Jx.StrOf(c, "ownedBy")})
            Next
            For Each s In Jx.ArrOf(j, "servers")
                m.Servers.Add(New ServerMcpInfo With {.Id = Jx.StrOf(s, "id"), .Name = Jx.StrOf(s, "name"), .Description = Jx.StrOf(s, "description"),
                                                      .Tools = Jx.IntOf(s, "tools"), .Connected = Jx.BoolOf(s, "connected")})
            Next
            Dim windows = Jx.Prop(j, "contextWindows")
            If windows.ValueKind = JsonValueKind.Object Then
                For Each p In windows.EnumerateObject()
                    If p.Value.ValueKind = JsonValueKind.Number Then m.ContextWindows(p.Name) = p.Value.GetInt32()
                Next
            End If
            Return m
        End Function

        Public Async Function ListAsync(ct As CancellationToken, Optional query As String = Nothing, Optional limit As Integer = 200, Optional bookmarkedOnly As Boolean = False) As Task(Of List(Of ChatSummary))
            Dim path = $"/api/chat/conversations?limit={limit}" & If(String.IsNullOrWhiteSpace(query), "", "&q=" & Enc(query)) & If(bookmarkedOnly, "&bookmarked=1", "")
            Dim j = Await _api.GetAsync(path, ct).ConfigureAwait(False)
            Dim list As New List(Of ChatSummary)
            If j.ValueKind = JsonValueKind.Array Then
                For Each c In j.EnumerateArray()
                    list.Add(SummaryOf(c))
                Next
            End If
            Return list
        End Function

        Private Shared Function SummaryOf(c As JsonElement) As ChatSummary
            Return New ChatSummary With {.Id = Jx.StrOf(c, "id"), .Title = Jx.StrOf(c, "title"), .Model = Jx.StrOf(c, "model"), .Pinned = Jx.BoolOf(c, "pinned"), .Bookmarked = Jx.BoolOf(c, "bookmarked"),
                                         .Archived = Jx.BoolOf(c, "archived"), .Live = Jx.BoolOf(c, "live"), .UpdatedUtc = Jx.DateOf(c, "updatedUtc"), .Preview = Jx.StrOf(c, "preview")}
        End Function

        ''' <summary>A new conversation; a temporary one (--no-session-persistence) is not kept in the history.</summary>
        ''' <param name="parent">A subagent's conversation: the chat and the Task call it works for (its answer streams into that call's card in the web app).</param>
        Public Async Function CreateAsync(title As String, model As String, settings As ServerSettings, ct As CancellationToken, Optional temporary As Boolean = False,
                                          Optional parent As Object = Nothing) As Task(Of Conversation)
            Dim body = "{""title"":" & Jx.Quote(title) & If(String.IsNullOrWhiteSpace(model), "", ",""model"":" & Jx.Quote(model)) &
                       If(settings Is Nothing, "", ",""settings"":" & settings.ToJson()) & If(temporary, ",""temporary"":true", "") &
                       If(parent Is Nothing, "", ",""parent"":" & Jx.Stringify(parent)) & "}"
            Return ConversationOf(Await _api.PostAsync("/api/chat/conversations", body, ct).ConfigureAwait(False))
        End Function

        ''' <summary>A copy of the conversation as a new one (/branch, --fork-session), up to messageId when given.</summary>
        Public Async Function ForkAsync(chatId As String, title As String, ct As CancellationToken, Optional messageId As String = Nothing,
                                        Optional parent As Object = Nothing) As Task(Of (Id As String, Title As String, Messages As Integer))
            ' parent: a forked subagent (Task with fork) streams into that Task card, like CreateAsync's.
            Dim j = Await _api.PostAsync("/api/chat/conversations/" & Enc(chatId) & "/fork", Jx.Stringify(New With {.title = title, .messageId = messageId, .parent = parent}), ct).ConfigureAwait(False)
            Return (Jx.StrOf(j, "id"), Jx.StrOf(j, "title"), Jx.IntOf(j, "messages"))
        End Function

        ''' <summary>
        ''' One answer from a model outside the conversation (/btw, /recap): nothing is added to the chat. messages are OpenAI
        ''' chat messages; returns the answer's text.
        ''' </summary>
        Public Async Function CompleteAsync(model As String, messages As IEnumerable(Of Object), maxTokens As Integer, ct As CancellationToken) As Task(Of String)
            Dim j = Await _api.PostAsync("/v1/chat/completions", Jx.Stringify(New With {.model = model, .stream = False, .max_tokens = maxTokens, .messages = messages.ToArray()}), ct, 300).ConfigureAwait(False)
            Dim choices = Jx.Prop(j, "choices")
            If choices.ValueKind <> JsonValueKind.Array OrElse choices.GetArrayLength() = 0 Then Return ""
            Return System.Text.RegularExpressions.Regex.Replace(Jx.StrOf(Jx.Prop(choices(0), "message"), "content"), "<think>[\s\S]*?</think>", "").Trim()
        End Function

        Public Async Function GetAsync(chatId As String, ct As CancellationToken) As Task(Of Conversation)
            Return ConversationOf(Await _api.GetAsync("/api/chat/conversations/" & Enc(chatId), ct).ConfigureAwait(False))
        End Function

        Public Function UpdateAsync(chatId As String, ct As CancellationToken, Optional title As String = Nothing, Optional model As String = Nothing,
                                    Optional archived As Boolean? = Nothing, Optional settings As ServerSettings = Nothing,
                                    Optional bookmarked As Boolean? = Nothing) As Task(Of JsonElement)
            Dim parts As New List(Of String)
            If bookmarked.HasValue Then parts.Add("""bookmarked"":" & If(bookmarked.Value, "true", "false"))
            If title IsNot Nothing Then parts.Add("""title"":" & Jx.Quote(title))
            If model IsNot Nothing Then parts.Add("""model"":" & Jx.Quote(model))
            If archived.HasValue Then parts.Add("""archived"":" & If(archived.Value, "true", "false"))
            If settings IsNot Nothing Then parts.Add("""settings"":" & settings.ToJson())
            Return _api.PatchAsync("/api/chat/conversations/" & Enc(chatId), "{" & String.Join(",", parts) & "}", ct)
        End Function

        ''' <summary>Remembers the model on the server for every client (older servers ignore it).</summary>
        Public Async Function SetLastModelAsync(model As String) As Task
            Try
                Await _api.SendAsync("PUT", "/api/chat/preferences", Jx.Stringify(New With {.lastModel = model}), CancellationToken.None, 15).ConfigureAwait(False)
            Catch ex As ApiException
            End Try
        End Function

        Public Function DeleteAsync(chatId As String, ct As CancellationToken) As Task(Of JsonElement)
            Return _api.DeleteAsync("/api/chat/conversations/" & Enc(chatId), ct)
        End Function

        Public Function OpenMessageStreamAsync(chatId As String, bodyJson As String, ct As CancellationToken) As Task(Of HttpResponseMessage)
            Return _api.OpenStreamAsync("/api/chat/conversations/" & Enc(chatId) & "/messages", bodyJson, ct)
        End Function

        ''' <summary>Reconnects to the answer being written in the chat after its stream dropped: what was missed, then the rest (or "ended").</summary>
        Public Function OpenAttachStreamAsync(chatId As String, ct As CancellationToken) As Task(Of HttpResponseMessage)
            Return _api.OpenStreamAsync("/api/chat/conversations/" & Enc(chatId) & "/attach", Nothing, ct)
        End Function

        Public Function ApproveAsync(chatId As String, approvalId As String, choice As String, ct As CancellationToken) As Task(Of JsonElement)
            Return _api.PostAsync("/api/chat/conversations/" & Enc(chatId) & "/approvals", Jx.Stringify(New With {.id = approvalId, .choice = choice}), ct)
        End Function

        ''' <summary>
        ''' Shows this CLI's permission question in every window of the user and waits for the first answer there (or for
        ''' ApproveAsync from this CLI): once | always | deny | timeout.
        ''' </summary>
        Public Async Function RequestApprovalAsync(chatId As String, approvalId As String, name As String, argsJson As String, reason As String, where As String,
                                                   ct As CancellationToken) As Task(Of String)
            Dim j = Await _api.PostAsync("/api/chat/conversations/" & Enc(chatId) & "/approvals/request",
                                         Jx.Stringify(New With {.id = approvalId, .name = name, .arguments = argsJson, .reason = reason, .where = where, .timeoutSeconds = 900}),
                                         ct, 960).ConfigureAwait(False)
            ' arguments: the user allowed the edit with some of its changes left out (in the web app); the call runs with these.
            Dim edited = Jx.StrOf(j, "arguments", "")
            If edited.Length > 0 Then ApprovedArguments(approvalId) = edited
            Return Jx.StrOf(j, "choice", "timeout")
        End Function

        ''' <summary>Arguments an approval came back with (some changes of an edit left out), by approval id; taken once.</summary>
        Public ReadOnly ApprovedArguments As New Concurrent.ConcurrentDictionary(Of String, String)(StringComparer.Ordinal)

        ''' <summary>The answer being written in the chat right now (for another window): {active, content, reasoning, tools, approvals}.</summary>
        Public Function LiveAsync(chatId As String, ct As CancellationToken) As Task(Of JsonElement)
            Return _api.GetAsync("/api/chat/conversations/" & Enc(chatId) & "/live", ct)
        End Function

        ''' <param name="image">A picture for the model (Read of an image); it is not added to the answer as an attachment (show false).</param>
        Public Function ToolResultAsync(chatId As String, callId As String, ok As Boolean, result As String, ct As CancellationToken,
                                        Optional imageMime As String = Nothing, Optional image As Byte() = Nothing, Optional imageName As String = Nothing,
                                        Optional images As List(Of (MediaType As String, Data As String)) = Nothing) As Task(Of JsonElement)
            Dim body As New Nodes.JsonObject From {{"id", callId}, {"ok", ok}, {"result", If(result, "")}}
            If image IsNot Nothing AndAlso image.Length > 0 Then
                body("image") = New Nodes.JsonObject From {{"mediaType", imageMime}, {"data", Convert.ToBase64String(image)}, {"name", imageName}, {"show", False}}
            End If
            If images IsNot Nothing AndAlso images.Count > 0 Then
                body("images") = New Nodes.JsonArray(images.Take(4).Select(Function(i) CType(New Nodes.JsonObject From {{"mediaType", i.MediaType}, {"data", i.Data}, {"show", True}}, Nodes.JsonNode)).ToArray())
            End If
            Return _api.PostAsync("/api/chat/conversations/" & Enc(chatId) & "/tool-results", body.ToJsonString(), ct)
        End Function

        Public Async Function ContextAsync(chatId As String, model As String, ct As CancellationToken) As Task(Of ContextInfo)
            Dim j = Await _api.GetAsync("/api/chat/conversations/" & Enc(chatId) & "/context" & If(String.IsNullOrWhiteSpace(model), "", "?model=" & Enc(model)), ct).ConfigureAwait(False)
            Return ContextInfo.From(j)
        End Function

        ''' <summary>
        ''' Compacts the conversation. The server streams the result as Server-Sent Events (keep-alive pings while it works,
        ''' then a final "done" event), so a long summary never trips Cloudflare's request read timeout (HTTP 524).
        ''' </summary>
        Public Async Function CompactAsync(chatId As String, ct As CancellationToken, Optional instructions As String = Nothing) As Task(Of (Ok As Boolean, Message As String, Context As ContextInfo, Stats As CompactStats))
            Dim body = If(String.IsNullOrWhiteSpace(instructions), "{}", Jx.Stringify(New With {.instructions = instructions}))
            Using resp = Await _api.OpenStreamAsync("/api/chat/conversations/" & Enc(chatId) & "/compact", body, ct).ConfigureAwait(False)
                Using stream = Await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(False)
                    Dim reader As New SseReader(stream)
                    While True
                        Dim ev = Await reader.NextAsync(ct).ConfigureAwait(False)
                        If ev Is Nothing Then Exit While
                        Dim e = ev.Value
                        If e.Name = "done" Then
                            Dim j = e.Json()
                            Return (Jx.BoolOf(j, "compacted"), Jx.StrOf(j, "message"), ContextInfo.From(Jx.Prop(j, "context")), CompactStats.From(Jx.Prop(j, "stats")))
                        ElseIf e.Name = "error" Then
                            Return (False, Jx.StrOf(e.Json(), "message", "Compaction failed."), Nothing, Nothing)
                        End If
                    End While
                End Using
            End Using
            Return (False, "The connection ended before compaction finished.", Nothing, Nothing)
        End Function

        Public Async Function StopAsync(chatId As String) As Task(Of Boolean)
            Try
                Dim j = Await _api.PostAsync("/api/chat/conversations/" & Enc(chatId) & "/stop", "{}", CancellationToken.None, 10).ConfigureAwait(False)
                Return Jx.BoolOf(j, "stopped")
            Catch ex As ApiException
                Return False
            End Try
        End Function

        Public Async Function RewindAsync(chatId As String, messageId As String, ct As CancellationToken) As Task(Of Integer)
            Dim j = Await _api.PostAsync("/api/chat/conversations/" & Enc(chatId) & "/rewind", Jx.Stringify(New With {.messageId = messageId}), ct).ConfigureAwait(False)
            Return Jx.IntOf(j, "removed")
        End Function

        ''' <summary>Uploads a file to attach to the next message; returns its id.</summary>
        Public Async Function UploadAsync(fileName As String, bytes As Byte(), ct As CancellationToken) As Task(Of (Id As String, Kind As String))
            Dim j = Await _api.PostBytesAsync("/api/uploads?name=" & Enc(fileName), bytes, ct).ConfigureAwait(False)
            Return (Jx.StrOf(j, "id"), Jx.StrOf(j, "kind"))
        End Function

        Friend Shared Function ConversationOf(j As JsonElement) As Conversation
            Dim c As New Conversation With {.Id = Jx.StrOf(j, "id"), .Title = Jx.StrOf(j, "title"), .Model = Jx.StrOf(j, "model"),
                                            .Settings = Jx.Prop(j, "settings"), .Summary = Jx.StrOf(j, "summary", Nothing)}
            For Each m In Jx.ArrOf(j, "messages")
                c.Messages.Add(MessageOf(m))
            Next
            Return c
        End Function

        Friend Shared Function MessageOf(m As JsonElement) As ChatMessage
            Return New ChatMessage With {
                .Id = Jx.StrOf(m, "id"), .Role = Jx.StrOf(m, "role"), .Content = Jx.StrOf(m, "content"), .Model = Jx.StrOf(m, "model"),
                .RoutedModel = Jx.StrOf(m, "routedModel", Nothing), .CreatedUtc = Jx.DateOf(m, "createdUtc"), .PromptTokens = Jx.IntOf(m, "promptTokens"),
                .CompletionTokens = Jx.IntOf(m, "completionTokens"), .DurationMs = Jx.LongOf(m, "durationMs"), .Error = Jx.StrOf(m, "error", Nothing),
                .ToolCalls = Jx.ArrOf(m, "toolCalls")}
        End Function

    End Class

End Namespace
