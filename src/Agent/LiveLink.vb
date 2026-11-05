Option Strict On
Option Explicit On
Option Infer On

Imports System.Collections.Concurrent
Imports System.Net.Http
Imports System.Security.Cryptography
Imports System.Text
Imports System.Text.Json
Imports System.Threading
Imports System.Threading.Channels
Imports AgenticCli.Api
Imports AgenticCli.Core

Namespace Agent

    ''' <summary>One event from the server's live stream: what happened in which chat.</summary>
    Friend NotInheritable Class HubItem
        Public Property ChatId As String
        Public Property Type As String
        Public Property Data As JsonElement
    End Class

    ''' <summary>
    ''' The CLI's connection to the server's live stream (/api/chat/stream), like a desktop app's: the server knows this CLI is
    ''' online and which tools it offers, so a message sent to this chat from the web app (or another window) can use the
    ''' CLI's tools. Events of answers written for other windows in the open chat, and tool calls for this CLI, wait in
    ''' Inbox until the session follows them; approvals answered elsewhere are remembered so open prompts can close.
    ''' </summary>
    Friend NotInheritable Class LiveLink
        Implements IDisposable

        Private ReadOnly _api As ApiClient
        Private ReadOnly _catalog As Func(Of String)
        Private ReadOnly _cts As New CancellationTokenSource()
        Private ReadOnly _inbox As Channel(Of HubItem) = Channel.CreateBounded(Of HubItem)(New BoundedChannelOptions(20000) With {.FullMode = BoundedChannelFullMode.DropOldest})
        ''' <summary>Tool calls for this CLI, apart from the events: they must run even while other events wait.</summary>
        Private ReadOnly _tools As Channel(Of HubItem) = Channel.CreateUnbounded(Of HubItem)()
        Private ReadOnly _resolved As New ConcurrentDictionary(Of String, String)(StringComparer.Ordinal)
        Private _loop As Task = Task.CompletedTask
        ''' <summary>This process (the same across reconnects of its stream): the server sends a tool call again to the same page first.</summary>
        Private ReadOnly _page As String = "p" & Guid.NewGuid().ToString("N").Substring(0, 12)
        ''' <summary>The streams this CLI has had (the server's subscriber ids): a tool call addressed to another window is not run here.</summary>
        Private ReadOnly _subs As New ConcurrentDictionary(Of String, Byte)(StringComparer.Ordinal)
        ''' <summary>Tool calls already taken: the server may send one again after the stream reconnected.</summary>
        Private ReadOnly _taken As New ConcurrentDictionary(Of String, Byte)(StringComparer.Ordinal)
        Private _watch As String
        Private _remoteRun As String
        Private _catchUp As Boolean

        Public ReadOnly Property DeviceId As String
        Public ReadOnly Property DeviceName As String
        ''' <summary>The id this CLI's own answers carry (TurnRunner.ClientId): their events are not followed here.</summary>
        Public ReadOnly Property ClientId As String
        Public ReadOnly Property Connected As Boolean
            Get
                Return _connected
            End Get
        End Property
        Private _connected As Boolean

        Public Sub New(api As ApiClient, deviceId As String, deviceName As String, clientId As String, catalog As Func(Of String))
            _api = api
            Me.DeviceId = deviceId
            Me.DeviceName = deviceName
            Me.ClientId = clientId
            _catalog = catalog
        End Sub

        ''' <summary>A stable id for this computer and project folder: chats remember it, so web messages reach this CLI after a restart.</summary>
        Public Shared Function MakeDeviceId(projectDir As String) As String
            Dim hash = SHA256.HashData(Encoding.UTF8.GetBytes(Environment.MachineName.ToLowerInvariant() & "|" & IO.Path.GetFullPath(projectDir).ToLowerInvariant()))
            Return "cli-" & Convert.ToHexString(hash).Substring(0, 16).ToLowerInvariant()
        End Function

        Public Sub Start()
            _loop = Task.Run(Function() RunAsync(_cts.Token))
        End Sub

        ''' <summary>The chat open in this session: its events from other windows are kept; earlier ones are dropped.</summary>
        Public Sub Watch(chatId As String)
            SyncLock _inbox
                If _watch = chatId Then Return
                _watch = chatId
                _remoteRun = Nothing
                _catchUp = False
                Dim dropped As HubItem = Nothing
                While _inbox.Reader.TryRead(dropped)
                End While
            End SyncLock
            ' A message may already be answered there for another window (the chat was opened while it runs).
            If Not String.IsNullOrEmpty(chatId) Then
                Dim unused = CheckLiveAsync(chatId)
            End If
        End Sub

        Private Async Function CheckLiveAsync(chatId As String) As Task
            Try
                Dim snap = Await _api.GetAsync("/api/chat/conversations/" & Uri.EscapeDataString(chatId) & "/live", _cts.Token).ConfigureAwait(False)
                If Jx.BoolOf(snap, "active") AndAlso Jx.StrOf(snap, "clientId", "") <> ClientId Then
                    SyncLock _inbox
                        If _watch = chatId AndAlso _remoteRun Is Nothing Then
                            _remoteRun = chatId
                            _catchUp = True
                        End If
                    End SyncLock
                End If
            Catch ex As Exception When TypeOf ex Is ApiException OrElse TypeOf ex Is HttpRequestException OrElse TypeOf ex Is OperationCanceledException
            End Try
        End Function

        ''' <summary>An answer for another window is being written in the watched chat (or its events wait in the inbox).</summary>
        Public ReadOnly Property RemoteRunActive As Boolean
            Get
                Return _remoteRun IsNot Nothing
            End Get
        End Property

        ''' <summary>True once: the session started following an answer that was already running (show what it wrote so far first).</summary>
        Public Function TakeCatchUp() As Boolean
            SyncLock _inbox
                Dim c = _catchUp
                _catchUp = False
                If c Then
                    ' The snapshot covers what was written; events already queued for this answer would repeat it.
                    Dim keep As New List(Of HubItem)
                    Dim item As HubItem = Nothing
                    While _inbox.Reader.TryRead(item)
                        If item.Type = "run" OrElse item.Type = "approval" OrElse item.Type = "approval_resolved" Then keep.Add(item)
                    End While
                    For Each k In keep
                        _inbox.Writer.TryWrite(k)
                    Next
                End If
                Return c
            End SyncLock
        End Function

        ''' <summary>Events that do not need the session's attention (they are handled or dropped here).</summary>
        Private Shared ReadOnly Quiet As New HashSet(Of String)(StringComparer.Ordinal) From {
            "approval_resolved", "changed", "compacted", "truncate", "deleted", "device", "title", "ping", "goal"}

        ''' <summary>
        ''' Something for the session to follow now: an answer for another window in the watched chat, or a tool call for this
        ''' CLI. Quiet events at the head of the inbox are taken off (titles go to onTitle).
        ''' </summary>
        Public Function HasWork(Optional onTitle As Action(Of String) = Nothing) As Boolean
            Return HasToolWork() OrElse HasChatWork(onTitle)
        End Function

        ''' <summary>A tool call for this CLI waits.</summary>
        Public Function HasToolWork() As Boolean
            Dim tool As HubItem = Nothing
            Return _tools.Reader.TryPeek(tool)
        End Function

        ''' <summary>An answer for another window in the watched chat is running, or its events wait.</summary>
        Public Function HasChatWork(Optional onTitle As Action(Of String) = Nothing) As Boolean
            If Not _connected AndAlso _remoteRun Is Nothing Then Return False
            SyncLock _inbox
                Dim item As HubItem = Nothing
                While _inbox.Reader.TryPeek(item)
                    If item.ChatId <> _watch OrElse Quiet.Contains(item.Type) OrElse (item.Type = "run" AndAlso Jx.StrOf(item.Data, "phase") <> "start" AndAlso _remoteRun Is Nothing) Then
                        _inbox.Reader.TryRead(item)
                        If item.Type = "title" AndAlso item.ChatId = _watch Then onTitle?.Invoke(Jx.StrOf(item.Data, "title"))
                        Continue While
                    End If
                    Return True
                End While
                Return _remoteRun IsNot Nothing
            End SyncLock
        End Function

        ''' <summary>
        ''' The next tool call for this CLI, or (includeEvents) the next event to follow; Nothing when cancelled or after waitMs
        ''' without one (waitMs 0 waits as long as it takes). Tool calls come first.
        ''' </summary>
        Public Async Function ReadAsync(ct As CancellationToken, Optional waitMs As Integer = 0, Optional includeEvents As Boolean = True) As Task(Of HubItem)
            Using cts = CancellationTokenSource.CreateLinkedTokenSource(ct)
                If waitMs > 0 Then cts.CancelAfter(waitMs)
                Try
                    While True
                        Dim item As HubItem = Nothing
                        If _tools.Reader.TryRead(item) Then Return item
                        If includeEvents AndAlso _inbox.Reader.TryRead(item) Then Return item
                        Dim waits As New List(Of Task) From {_tools.Reader.WaitToReadAsync(cts.Token).AsTask()}
                        If includeEvents Then waits.Add(_inbox.Reader.WaitToReadAsync(cts.Token).AsTask())
                        Await Task.WhenAny(waits).ConfigureAwait(False)
                        cts.Token.ThrowIfCancellationRequested()
                    End While
                Catch ex As OperationCanceledException
                    Return Nothing
                Catch ex As ChannelClosedException
                    Return Nothing
                End Try
            End Using
            Return Nothing
        End Function

        ''' <summary>The session stopped following (the answer ended, or was stopped): a new one starts with its own run event.</summary>
        Public Sub FollowEnded()
            SyncLock _inbox
                _remoteRun = Nothing
                _catchUp = False
            End SyncLock
        End Sub

        ''' <summary>The approval was answered (in any window): its choice, else Nothing.</summary>
        Public Function ResolvedChoice(approvalId As String) As String
            Dim c As String = Nothing
            Return If(_resolved.TryGetValue(If(approvalId, ""), c), c, Nothing)
        End Function

        ''' <summary>Tells the server which tools this CLI offers (again after MCP servers connect or the chat changes).</summary>
        Public Sub PushCatalog()
            Dim unused = Task.Run(Async Function()
                                      Try
                                          Await _api.PostAsync("/api/chat/devices/" & Uri.EscapeDataString(DeviceId) & "/catalog", _catalog.Invoke(), _cts.Token, 30).ConfigureAwait(False)
                                      Catch ex As Exception When TypeOf ex Is ApiException OrElse TypeOf ex Is HttpRequestException OrElse TypeOf ex Is OperationCanceledException OrElse TypeOf ex Is InvalidOperationException
                                      End Try
                                  End Function)
        End Sub

        Private Async Function RunAsync(ct As CancellationToken) As Task
            Dim failures = 0
            While Not ct.IsCancellationRequested
                Try
                    Using resp = Await _api.OpenStreamAsync($"/api/chat/stream?device={Uri.EscapeDataString(DeviceId)}&name={Uri.EscapeDataString(DeviceName)}&page={_page}", Nothing, ct).ConfigureAwait(False)
                        Using stream = Await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(False)
                            Dim reader As New SseReader(stream)
                            While Not ct.IsCancellationRequested
                                Dim ev = Await reader.NextAsync(ct).ConfigureAwait(False)
                                If Not ev.HasValue Then Exit While
                                If ev.Value.Name = "hello" Then
                                    _connected = True
                                    failures = 0
                                    OnHello(ev.Value.Json())
                                    PushCatalog()
                                ElseIf ev.Value.Name = "chat" Then
                                    OnChat(ev.Value.Json())
                                End If
                            End While
                        End Using
                    End Using
                Catch ex As Exception When TypeOf ex Is ApiException OrElse TypeOf ex Is HttpRequestException OrElse TypeOf ex Is IO.IOException OrElse
                                           (TypeOf ex Is OperationCanceledException AndAlso Not ct.IsCancellationRequested)
                End Try
                _connected = False
                If ct.IsCancellationRequested Then Exit While
                failures += 1
                Try
                    Await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, 2 * failures)), ct).ConfigureAwait(False)
                Catch ex As OperationCanceledException
                    Exit While
                End Try
            End While
        End Function

        Private Sub OnHello(d As JsonElement)
            Dim [sub] = Jx.StrOf(d, "subscriber", "")
            If [sub].Length > 0 Then _subs([sub]) = 0
            SyncLock _inbox
                Dim live = Jx.ArrOf(d, "runs").FirstOrDefault(Function(r) Jx.StrOf(r, "chatId") = _watch AndAlso Jx.StrOf(r, "clientId", "") <> ClientId)
                Dim isLive = live.ValueKind = JsonValueKind.Object
                If isLive AndAlso _remoteRun Is Nothing AndAlso Not String.IsNullOrEmpty(_watch) Then
                    ' Running for another window, and its start was missed (this CLI was offline): show it from a snapshot.
                    _remoteRun = _watch
                    _catchUp = True
                ElseIf Not isLive AndAlso _remoteRun IsNot Nothing Then
                    ' It ended while this CLI was offline: close the followed answer (the session loads what was saved).
                    _inbox.Writer.TryWrite(New HubItem With {.ChatId = _remoteRun, .Type = "run", .Data = Jx.Parse("{""phase"":""end"",""missed"":true}")})
                    _remoteRun = Nothing
                End If
            End SyncLock
        End Sub

        Private Sub OnChat(m As JsonElement)
            Dim chatId = Jx.StrOf(m, "chatId", "")
            Dim type = Jx.StrOf(m, "type", "")
            Dim data = Jx.Prop(m, "data").Clone()
            Dim fromClient = Jx.StrOf(m, "clientId", "")
            If type = "approval_resolved" Then _resolved(Jx.StrOf(data, "id")) = Jx.StrOf(data, "choice", "deny")
            If type = "mcp_install" Then
                ' "Install on my computer" in the web app: the server goes to ~/.agentic/mcp.json switched off, for the user to check
                ' and turn on (/mcp enable), since nothing asks here.
                If Jx.StrOf(data, "device", "") <> DeviceId OrElse Not _taken.TryAdd("mcp:" & Jx.StrOf(data, "id", ""), 0) Then Return
                Dim name = Jx.StrOf(data, "name", "server")
                Dim parsed = Mcp.McpConfig.Parse("{""mcpServers"":{" & Jx.Quote(name) & ":" & Jx.Prop(data, "entry").GetRawText() & "}}")
                If parsed.Count = 0 Then Return
                parsed(0).Enabled = False
                Mcp.McpConfig.Upsert(parsed(0), "user", Environment.CurrentDirectory)
                Ui.Out.Note($"The web app sent the MCP server {parsed(0).Id}: added to ~/.agentic/mcp.json, switched off. Turn it on with: agentic mcp enable {parsed(0).Id} (new sessions use it).")
                Return
            End If
            If type = "client_tool" Then
                ' A tool call for this CLI (a message sent from another window, or this CLI's own answer while its stream dropped).
                ' Only calls for this folder's device AND this CLI's stream (two CLIs in one folder must not both run it), each once.
                If Jx.StrOf(data, "device", "") <> DeviceId Then Return
                Dim target = Jx.StrOf(data, "target", "")
                If target.Length > 0 AndAlso Not _subs.IsEmpty AndAlso Not _subs.ContainsKey(target) Then Return
                If Not _taken.TryAdd(Jx.StrOf(data, "id", ""), 0) Then Return
                _tools.Writer.TryWrite(New HubItem With {.ChatId = chatId, .Type = type, .Data = data})
                Return
            End If
            If fromClient.Length > 0 AndAlso fromClient = ClientId Then Return     ' this CLI's own answer: it has its own stream
            SyncLock _inbox
                If chatId <> _watch Then Return
                If type = "run" Then
                    If Jx.StrOf(data, "phase") = "start" Then
                        _remoteRun = chatId
                    Else
                        _remoteRun = Nothing
                    End If
                End If
                _inbox.Writer.TryWrite(New HubItem With {.ChatId = chatId, .Type = type, .Data = data})
            End SyncLock
        End Sub

        Public Sub Dispose() Implements IDisposable.Dispose
            _cts.Cancel()
            Try
                _loop.Wait(2000)
            Catch ex As AggregateException
            End Try
            _cts.Dispose()
        End Sub

    End Class

End Namespace
