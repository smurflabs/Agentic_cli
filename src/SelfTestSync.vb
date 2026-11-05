Option Strict On
Option Explicit On
Option Infer On

Imports System.IO
Imports System.Net
Imports System.Net.Sockets
Imports System.Text
Imports System.Threading
Imports AgenticCli.Agent
Imports AgenticCli.Api
Imports AgenticCli.Core
Imports AgenticCli.Tools
Imports AgenticCli.Ui

''' <summary>Self-tests of web app sync: the box and menus close when another window acts; the live stream is followed.</summary>
Partial Friend NotInheritable Class SelfTest

    Private Shared Sub SyncUiTests()
        ' Another window sends a message: the box closes and keeps what was typed.
        Dim term As New FakeTerminal()
        term.Type("abc")
        Dim polls = 0
        Dim ed As New LineEditor(term) With {.Interrupt = Function() term.Keys.Count = 0 AndAlso Interlocked.Increment(polls) >= 2}
        Dim text = ed.ReadLine()
        Check("sync: the box closes for a message from another window, keeping the typed text", text Is Nothing AndAlso ed.Interrupted AndAlso ed.InterruptedText = "abc",
              $"text={text} interrupted={ed.Interrupted} kept={ed.InterruptedText}")

        ' A question or approval answered in the web app closes the menu here.
        Dim saved = Menu.Term
        Try
            Menu.Term = New FakeTerminal()
            Dim asks = 0
            Menu.Abort = Function() Interlocked.Increment(asks) >= 3
            Dim pick = Menu.Select("Pick", {New MenuOption("A"), New MenuOption("B")})
            Menu.Abort = Nothing
            Check("sync: a menu answered elsewhere closes", pick = -1 AndAlso Menu.Aborted, $"pick={pick} aborted={Menu.Aborted}")
            Dim keys As New FakeTerminal()
            keys.Type("2")
            Menu.Term = keys
            Menu.Abort = Function() False
            Dim pick2 = Menu.Select("Pick", {New MenuOption("A"), New MenuOption("B")})
            Menu.Abort = Nothing
            Check("sync: a menu answered here is not aborted", pick2 = 1 AndAlso Not Menu.Aborted, $"pick={pick2}")
        Finally
            Menu.Abort = Nothing
            Menu.Term = saved
        End Try
    End Sub

    ''' <summary>
    ''' A fake server: the live stream announces a message sent from the web app in chat c1, its answer, and a tool call for
    ''' this CLI; the CLI follows it, runs the tool and sends the result.
    ''' </summary>
    Private Shared Async Function SyncLinkTests(dir As String) As Task
        Dim port As Integer
        Using probe As New TcpListener(IPAddress.Loopback, 0)
            probe.Start()
            port = CType(probe.LocalEndpoint, IPEndPoint).Port
        End Using
        Dim file1 = Path.Combine(dir, "sync.txt")
        File.WriteAllText(file1, "the secret is 42")
        Dim listener As New HttpListener()
        listener.Prefixes.Add($"http://localhost:{port}/")
        Try
            listener.Start()
        Catch ex As HttpListenerException
            Check("sync: fake server starts", False, ex.Message)
            Return
        End Try
        Dim toolResult As New TaskCompletionSource(Of String)(TaskCreationOptions.RunContinuationsAsynchronously)
        Dim catalog As New TaskCompletionSource(Of String)(TaskCreationOptions.RunContinuationsAsynchronously)
        Dim serving = Task.Run(Async Function()
                                   While listener.IsListening
                                       Dim ctx As HttpListenerContext
                                       Try
                                           ctx = Await listener.GetContextAsync().ConfigureAwait(False)
                                       Catch ex As Exception When TypeOf ex Is HttpListenerException OrElse TypeOf ex Is ObjectDisposedException OrElse TypeOf ex Is InvalidOperationException
                                           Exit While
                                       End Try
                                       Dim unused = Task.Run(Function() ServeAsync(ctx, file1, toolResult, catalog))
                                   End While
                               End Function)
        Dim api As New ApiClient($"http://localhost:{port}", "test-key")
        Dim link As New LiveLink(api, "cli-test", "CLI · test", "cli-own", Function() "{""name"":""CLI · test"",""clientTools"":[]}")
        Try
            link.Watch("c1")
            link.Start()
            Dim waited = 0
            While Not link.HasWork() AndAlso waited < 100
                Await Task.Delay(50)
                waited += 1
            End While
            Check("sync: a message from the web app is noticed", link.HasWork())
            Check("sync: the catalog is sent on connect", (Await Task.WhenAny(catalog.Task, Task.Delay(3000))) Is catalog.Task)
            Dim state = NewState(dir)
            state.ChatId = "c1"
            Dim runner As New TurnRunner(New ChatApi(api), state, New PermissionEngine(state)) With {.ClientId = "cli-own", .Link = link}
            Dim o As New TurnOptions With {.ChatId = "c1", .Follow = True, .Tools = New List(Of ITool) From {New ReadTool()}, .Render = False}
            Using cts As New CancellationTokenSource(TimeSpan.FromSeconds(20))
                Dim r = Await runner.RunAsync(o, cts.Token)
                Check("sync: the followed message and answer", r.Prompt = "what is the secret?" AndAlso r.Text.ToString() = "Reading. It is 42." AndAlso r.Error Is Nothing AndAlso Not r.Cancelled,
                      $"prompt={r.Prompt} text={r.Text} error={r.Error} cancelled={r.Cancelled}")
                Check("sync: events of this CLI's own answers are not followed", Not r.Text.ToString().Contains("OWN"))
                Check("sync: the tool ran here and its result went back", toolResult.Task.IsCompleted AndAlso toolResult.Task.Result.Contains("the secret is 42"),
                      If(toolResult.Task.IsCompleted, toolResult.Task.Result, "no result"))
                Check("sync: an approval answered in the web app is known here", link.ResolvedChoice("ap1") = "once")
                Check("thinking: each thinking phase is kept apart (before and after the tool)",
                      r.Thoughts.Count = 2 AndAlso r.Thoughts(0) = "I should read the file first." AndAlso r.Thoughts(1) = "The file says 42.", String.Join(" | ", r.Thoughts))
            End Using
            link.FollowEnded()
            Check("sync: nothing left to follow afterwards", Not link.HasWork())
        Finally
            link.Dispose()
            listener.Stop()
            listener.Close()
        End Try
    End Function

    Private Shared Async Function ServeAsync(ctx As HttpListenerContext, file1 As String, toolResult As TaskCompletionSource(Of String), catalog As TaskCompletionSource(Of String)) As Task
        Dim path1 = ctx.Request.Url.AbsolutePath
        Dim reply = Async Function(json As String) As Task
                        Dim bytes = Encoding.UTF8.GetBytes(json)
                        ctx.Response.ContentType = "application/json"
                        ctx.Response.ContentLength64 = bytes.Length
                        Await ctx.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(False)
                        ctx.Response.Close()
                    End Function
        Try
            If path1 = "/api/chat/stream" Then
                ctx.Response.ContentType = "text/event-stream"
                ctx.Response.SendChunked = True
                Dim w As New StreamWriter(ctx.Response.OutputStream, New UTF8Encoding(False)) With {.AutoFlush = True, .NewLine = vbLf}
                Dim send = Async Function(name As String, data As String) As Task
                               Await w.WriteAsync($"event: {name}" & vbLf & "data: " & data & vbLf & vbLf).ConfigureAwait(False)
                           End Function
                Dim chat = Function(chatId As String, type As String, data As String, clientId As String) _
                               $"{{""chatId"":""{chatId}"",""type"":""{type}"",""data"":{data},""clientId"":{If(clientId Is Nothing, "null", """" & clientId & """")}}}"
                Await send("hello", "{""runs"":[],""devices"":[]}")
                Await send("chat", chat("c1", "run", "{""phase"":""start"",""model"":""m""}", "w1"))
                Await send("chat", chat("c1", "user", "{""id"":""u1"",""role"":""user"",""content"":""what is the secret?""}", "w1"))
                Await send("chat", chat("c1", "delta", "{""text"":""OWN""}", "cli-own"))              ' this CLI's own answer: ignored
                Await send("chat", chat("c2", "delta", "{""text"":""OTHER""}", "w1"))                 ' another chat: ignored
                Await send("chat", chat("c1", "reasoning", "{""text"":""I should read the file first.""}", "w1"))
                Await send("chat", chat("c1", "delta", "{""text"":""Reading. ""}", "w1"))
                Await send("chat", chat("c1", "approval_resolved", "{""id"":""ap1"",""choice"":""once""}", "w1"))
                Dim args = Jx.Stringify(New With {.file_path = file1})
                Await send("chat", chat("c1", "client_tool", Jx.Stringify(New With {.id = "t1", .name = "Read", .arguments = args, .device = "cli-test", .approved = True}), Nothing))
                Await Task.WhenAny(toolResult.Task, Task.Delay(10000)).ConfigureAwait(False)
                Await send("chat", chat("c1", "reasoning", "{""text"":""The file says 42.""}", "w1"))
                Await send("chat", chat("c1", "delta", "{""text"":""It is 42.""}", "w1"))
                Await send("chat", chat("c1", "done", "{""message"":{""id"":""m1"",""content"":""Reading. It is 42."",""promptTokens"":10,""completionTokens"":5}}", "w1"))
                Await send("chat", chat("c1", "run", "{""phase"":""end""}", "w1"))
                ' Stay open like the server does (pings) until the CLI goes away.
                While True
                    Await Task.Delay(500).ConfigureAwait(False)
                    Await w.WriteAsync(": ping" & vbLf & vbLf).ConfigureAwait(False)
                End While
            ElseIf path1.EndsWith("/catalog") Then
                Using r As New StreamReader(ctx.Request.InputStream)
                    catalog.TrySetResult(Await r.ReadToEndAsync().ConfigureAwait(False))
                End Using
                Await reply("{""tools"":0}")
            ElseIf path1.EndsWith("/tool-results") Then
                Using r As New StreamReader(ctx.Request.InputStream)
                    toolResult.TrySetResult(Await r.ReadToEndAsync().ConfigureAwait(False))
                End Using
                Await reply("{""accepted"":true}")
            ElseIf path1.EndsWith("/live") Then
                Await reply("{""active"":false}")
            Else
                ctx.Response.StatusCode = 404
                Await reply("{}")
            End If
        Catch ex As Exception When TypeOf ex Is HttpListenerException OrElse TypeOf ex Is IOException OrElse TypeOf ex Is ObjectDisposedException OrElse TypeOf ex Is InvalidOperationException
        End Try
    End Function

End Class
