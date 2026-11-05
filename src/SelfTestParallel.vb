Option Strict On
Option Explicit On
Option Infer On

Imports System.Collections.Concurrent
Imports System.IO
Imports System.Net
Imports System.Net.Sockets
Imports System.Text
Imports System.Text.Json
Imports System.Text.Json.Nodes
Imports System.Threading
Imports AgenticCli.Agent
Imports AgenticCli.Api
Imports AgenticCli.Core
Imports AgenticCli.Tools

''' <summary>
''' Self-tests of parallel agents and tool routing: a round's read-only calls (and subagents) run at the same time; a tool
''' call addressed to another window of the same folder is not run here, and a call the server sends again runs once.
''' </summary>
Partial Friend NotInheritable Class SelfTest

    ''' <summary>A read-only tool that records how many of its calls run at the same time.</summary>
    Private NotInheritable Class ProbeTool
        Implements ITool
        Private _now As Integer
        Public MaxAtOnce As Integer

        Public ReadOnly Property Name As String = "Probe" Implements ITool.Name
        Public ReadOnly Property Description As String = "Probe" Implements ITool.Description
        Public ReadOnly Property IsReadOnly As Boolean = True Implements ITool.IsReadOnly

        Public Function Parameters() As JsonObject Implements ITool.Parameters
            Return New JsonObject From {{"type", "object"}, {"properties", New JsonObject()}}
        End Function

        Public Function Title(input As JsonElement, state As AgentState) As String Implements ITool.Title
            Return "Probe()"
        End Function

        Public Function Preview(input As JsonElement, state As AgentState) As String Implements ITool.Preview
            Return Nothing
        End Function

        Public Async Function RunAsync(input As JsonElement, ctx As ToolContext) As Task(Of ToolResult) Implements ITool.RunAsync
            Dim n = Interlocked.Increment(_now)
            SyncLock Me
                If n > MaxAtOnce Then MaxAtOnce = n
            End SyncLock
            ' Long enough for the round's other call to start alongside; short enough that the one-at-a-time run stays quick.
            Await Task.Delay(400).ConfigureAwait(False)
            Interlocked.Decrement(_now)
            Return ToolResult.Done("probed", "probed")
        End Function
    End Class

    Private Shared Function FreePort() As Integer
        Using probe As New TcpListener(IPAddress.Loopback, 0)
            probe.Start()
            Return CType(probe.LocalEndpoint, IPEndPoint).Port
        End Using
    End Function

    Private Shared Async Function ReplyAsync(ctx As HttpListenerContext, json As String) As Task
        Dim bytes = Encoding.UTF8.GetBytes(json)
        ctx.Response.ContentType = "application/json"
        ctx.Response.ContentLength64 = bytes.Length
        Await ctx.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(False)
        ctx.Response.Close()
    End Function

    ''' <summary>The server sends two read-only calls of one round back to back; with Parallel on they overlap, off they do not.</summary>
    Private Shared Async Function ParallelToolTests(dir As String) As Task
        For Each parallel In {True, False}
            Dim port = FreePort()
            Dim listener As New HttpListener()
            listener.Prefixes.Add($"http://localhost:{port}/")
            Try
                listener.Start()
            Catch ex As HttpListenerException
                Check("parallel: fake server starts", False, ex.Message)
                Return
            End Try
            Dim results As New ConcurrentDictionary(Of String, Byte)(StringComparer.Ordinal)
            Dim allIn As New TaskCompletionSource(Of Boolean)(TaskCreationOptions.RunContinuationsAsynchronously)
            Dim serve = Async Function(ctx As HttpListenerContext) As Task
                            Dim p = ctx.Request.Url.AbsolutePath
                            Try
                                If p = "/api/chat/conversations/c9/messages" Then
                                    ctx.Response.ContentType = "text/event-stream"
                                    ctx.Response.SendChunked = True
                                    Dim w As New StreamWriter(ctx.Response.OutputStream, New UTF8Encoding(False)) With {.AutoFlush = True, .NewLine = vbLf}
                                    Dim send = Async Function(name As String, data As String) As Task
                                                   Await w.WriteAsync($"event: {name}" & vbLf & "data: " & data & vbLf & vbLf).ConfigureAwait(False)
                                               End Function
                                    Await send("start", "{""messageId"":""m9""}")
                                    Await send("client_tool", "{""id"":""t1"",""name"":""Probe"",""arguments"":""{}""}")
                                    Await send("client_tool", "{""id"":""t2"",""name"":""Probe"",""arguments"":""{}""}")
                                    Await Task.WhenAny(allIn.Task, Task.Delay(10000)).ConfigureAwait(False)
                                    Await send("done", "{""message"":{""id"":""m9""},""stopReason"":""end""}")
                                    w.Dispose()
                                    ctx.Response.Close()
                                ElseIf p.EndsWith("/tool-results", StringComparison.Ordinal) Then
                                    Dim body As String
                                    Using r As New StreamReader(ctx.Request.InputStream)
                                        body = Await r.ReadToEndAsync().ConfigureAwait(False)
                                    End Using
                                    results(Jx.StrOf(Jx.Parse(body), "id", "")) = 0
                                    If results.Count >= 2 Then allIn.TrySetResult(True)
                                    Await ReplyAsync(ctx, "{}").ConfigureAwait(False)
                                Else
                                    Await ReplyAsync(ctx, "{}").ConfigureAwait(False)
                                End If
                            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is HttpListenerException OrElse TypeOf ex Is ObjectDisposedException
                            End Try
                        End Function
            Dim serving = Task.Run(Async Function()
                                       While listener.IsListening
                                           Dim ctx As HttpListenerContext
                                           Try
                                               ctx = Await listener.GetContextAsync().ConfigureAwait(False)
                                           Catch ex As Exception When TypeOf ex Is HttpListenerException OrElse TypeOf ex Is ObjectDisposedException OrElse TypeOf ex Is InvalidOperationException
                                               Exit While
                                           End Try
                                           Dim unused = Task.Run(Function() serve(ctx))
                                       End While
                                   End Function)
            Try
                Dim api As New ApiClient($"http://localhost:{port}", "test-key")
                Dim state = NewState(dir)
                state.ChatId = "c9"
                state.Settings.SetValue("local", "ccusageLog", JsonValue.Create(False))
                Dim probe As New ProbeTool()
                Dim runner As New TurnRunner(New ChatApi(api), state, New PermissionEngine(state)) With {.ClientId = "cli-par", .Parallel = parallel}
                Dim o As New TurnOptions With {.ChatId = "c9", .Content = "go", .Tools = New List(Of ITool) From {probe}, .Render = False}
                Using cts As New CancellationTokenSource(TimeSpan.FromSeconds(20))
                    Dim r = Await runner.RunAsync(o, cts.Token)
                    Dim both = results.ContainsKey("t1") AndAlso results.ContainsKey("t2")
                    If parallel Then
                        Check("parallel: a round's read-only calls run at the same time", both AndAlso probe.MaxAtOnce = 2 AndAlso r.Error Is Nothing AndAlso r.ToolUses = 2,
                              $"maxAtOnce={probe.MaxAtOnce} results={String.Join(",", results.Keys)} error={r.Error} uses={r.ToolUses}")
                    Else
                        Check("parallel: off runs them one at a time", both AndAlso probe.MaxAtOnce = 1 AndAlso r.Error Is Nothing,
                              $"maxAtOnce={probe.MaxAtOnce} results={String.Join(",", results.Keys)} error={r.Error}")
                    End If
                End Using
            Finally
                listener.Stop()
                listener.Close()
            End Try
        Next
    End Function

    ''' <summary>The live stream: a call for another window of this folder is skipped; one sent twice runs once; other devices' calls are ignored.</summary>
    Private Shared Async Function LinkTargetTests() As Task
        Dim port = FreePort()
        Dim listener As New HttpListener()
        listener.Prefixes.Add($"http://localhost:{port}/")
        Try
            listener.Start()
        Catch ex As HttpListenerException
            Check("routing: fake server starts", False, ex.Message)
            Return
        End Try
        Dim serve = Async Function(ctx As HttpListenerContext) As Task
                        Try
                            If ctx.Request.Url.AbsolutePath = "/api/chat/stream" Then
                                ctx.Response.ContentType = "text/event-stream"
                                ctx.Response.SendChunked = True
                                Dim w As New StreamWriter(ctx.Response.OutputStream, New UTF8Encoding(False)) With {.AutoFlush = True, .NewLine = vbLf}
                                Dim send = Async Function(name As String, data As String) As Task
                                               Await w.WriteAsync($"event: {name}" & vbLf & "data: " & data & vbLf & vbLf).ConfigureAwait(False)
                                           End Function
                                Dim tool = Function(id As String, device As String, target As String) _
                                               $"{{""chatId"":""c1"",""type"":""client_tool"",""data"":{{""id"":""{id}"",""name"":""Read"",""arguments"":""{{}}"",""device"":""{device}"",""target"":""{target}""}},""clientId"":null}}"
                                Await send("hello", "{""runs"":[],""devices"":[],""subscriber"":""sub_me""}")
                                Await send("chat", tool("x1", "cli-t", "sub_other"))
                                Await send("chat", tool("x2", "cli-t", "sub_me"))
                                Await send("chat", tool("x2", "cli-t", "sub_me"))
                                Await send("chat", tool("x3", "cli-elsewhere", "sub_me"))
                                Await Task.Delay(4000).ConfigureAwait(False)
                                w.Dispose()
                                ctx.Response.Close()
                            Else
                                Await ReplyAsync(ctx, "{}").ConfigureAwait(False)
                            End If
                        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is HttpListenerException OrElse TypeOf ex Is ObjectDisposedException
                        End Try
                    End Function
        Dim serving = Task.Run(Async Function()
                                   While listener.IsListening
                                       Dim ctx As HttpListenerContext
                                       Try
                                           ctx = Await listener.GetContextAsync().ConfigureAwait(False)
                                       Catch ex As Exception When TypeOf ex Is HttpListenerException OrElse TypeOf ex Is ObjectDisposedException OrElse TypeOf ex Is InvalidOperationException
                                           Exit While
                                       End Try
                                       Dim unused = Task.Run(Function() serve(ctx))
                                   End While
                               End Function)
        Dim link As New LiveLink(New ApiClient($"http://localhost:{port}", "test-key"), "cli-t", "CLI · t", "cli-own", Function() "{""name"":""CLI · t"",""clientTools"":[]}")
        Try
            link.Start()
            Dim got As New List(Of String)
            Using cts As New CancellationTokenSource(TimeSpan.FromSeconds(8))
                While True
                    Dim item = Await link.ReadAsync(cts.Token, waitMs:=1500, includeEvents:=False)
                    If item Is Nothing Then Exit While
                    got.Add(Jx.StrOf(item.Data, "id", ""))
                End While
            End Using
            Check("routing: only the call addressed to this CLI runs, once", got.Count = 1 AndAlso got(0) = "x2", String.Join(",", got))
        Finally
            link.Dispose()
            listener.Stop()
            listener.Close()
        End Try
    End Function

    ''' <summary>Subagents in the terminal: each step's writing is printed tagged with its name, long text cut to a few lines.</summary>
    Private Shared Sub SubagentViewTests(tmp As String)
        Dim view As New SubagentView(NewState(tmp))
        Dim sw As New StringWriter()
        Dim old = Console.Out
        Console.SetOut(sw)
        Try
            view.Start("c1", "Scan configs")
            view.Write("c1", "Scan configs", "Thinking about where to look", thinking:=True)
            view.Write("c1", "Scan configs", "I will look at ", thinking:=False)
            view.Write("c1", "Scan configs", "appsettings.json.", thinking:=False)
            view.Flush("c1")
            view.Write("c1", Nothing, String.Join(vbLf, Enumerable.Range(1, 8).Select(Function(i) "Report line " & i)), thinking:=False)
            view.Finish("c1")
        Finally
            Console.SetOut(old)
        End Try
        Dim text = sw.ToString()
        Check("subagents: a step's writing is printed tagged with the subagent's name", text.Contains("[Scan configs] I will look at appsettings.json."), text)
        Check("subagents: thinking shows only live (not printed)", Not text.Contains("Thinking about"), text)
        Check("subagents: a long report is cut to a few lines, with how to see it all", text.Contains("Report line 6") AndAlso Not text.Contains("Report line 7") AndAlso text.Contains("2 more lines"), text)
    End Sub

End Class
