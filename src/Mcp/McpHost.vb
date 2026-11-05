Option Strict On
Option Explicit On
Option Infer On

Namespace Mcp

    ''' <summary>
    ''' The CLI's MCP servers: connects the enabled ones in parallel at start (project servers only once approved), reconnects
    ''' on request and answers tool queries for the chat.
    ''' </summary>
    Friend NotInheritable Class McpHost
        Implements IDisposable

        Private ReadOnly _lock As New Object()
        Private ReadOnly _connections As New Dictionary(Of String, McpConnection)(StringComparer.OrdinalIgnoreCase)
        Private _configured As New List(Of McpServerOptions)
        Public Property Roots As New List(Of String)

        Public Sub Configure(servers As List(Of McpServerOptions))
            SyncLock _lock
                _configured = servers
            End SyncLock
        End Sub

        Public Function Configured() As List(Of McpServerOptions)
            SyncLock _lock
                Return _configured.ToList()
            End SyncLock
        End Function

        ''' <summary>Connects every enabled server that passes the filter (e.g. approved project servers).</summary>
        Public Async Function ConnectAllAsync(filter As Func(Of McpServerOptions, Boolean)) As Task
            Dim tasks = Configured().Where(Function(s) s.Enabled AndAlso (filter Is Nothing OrElse filter(s))).Select(AddressOf ConnectOneAsync).ToArray()
            If tasks.Length > 0 Then Await Task.WhenAll(tasks).ConfigureAwait(False)
        End Function

        Public Async Function ConnectOneAsync(options As McpServerOptions) As Task
            Dim connection As New McpConnection(options) With {.Roots = Roots.ToList()}
            Dim previous As McpConnection = Nothing
            SyncLock _lock
                _connections.TryGetValue(options.Id, previous)
                _connections(options.Id) = connection
            End SyncLock
            previous?.Dispose()
            Await connection.StartAsync().ConfigureAwait(False)
        End Function

        Public Async Function ReconnectAsync(serverId As String) As Task
            Dim options = Configured().FirstOrDefault(Function(s) s.Id.Equals(serverId, StringComparison.OrdinalIgnoreCase))
            If options Is Nothing Then
                Disconnect(serverId)
                Return
            End If
            Await ConnectOneAsync(options).ConfigureAwait(False)
        End Function

        Public Sub Disconnect(serverId As String)
            Dim c As McpConnection = Nothing
            SyncLock _lock
                If _connections.TryGetValue(serverId, c) Then _connections.Remove(serverId)
            End SyncLock
            c?.Dispose()
        End Sub

        Public Function [Get](serverId As String) As McpConnection
            SyncLock _lock
                Dim c As McpConnection = Nothing
                Return If(_connections.TryGetValue(serverId, c), c, Nothing)
            End SyncLock
        End Function

        Public Function Connections() As List(Of McpConnection)
            SyncLock _lock
                Return _connections.Values.ToList()
            End SyncLock
        End Function

        Public Function ConnectedServers() As List(Of McpConnection)
            Return Connections().Where(Function(c) c.Status.Connected).ToList()
        End Function

        Public Async Function CallToolAsync(serverId As String, toolName As String, argumentsJson As String) As Task(Of McpToolResult)
            Dim connection = [Get](serverId)
            If connection Is Nothing OrElse Not connection.Status.Connected Then
                Return New McpToolResult With {.Ok = False, .IsError = True, .Error = $"MCP server '{serverId}' is not connected."}
            End If
            Return Await connection.CallToolAsync(toolName, argumentsJson).ConfigureAwait(False)
        End Function

        Public Sub Dispose() Implements IDisposable.Dispose
            Dim all As List(Of McpConnection)
            SyncLock _lock
                all = _connections.Values.ToList()
                _connections.Clear()
            End SyncLock
            For Each c In all
                c.Dispose()
            Next
        End Sub

    End Class

End Namespace
