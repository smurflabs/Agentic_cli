Option Strict On
Option Explicit On
Option Infer On

Imports System.IO
Imports System.Text
Imports System.Text.Json
Imports System.Text.Json.Nodes
Imports System.Threading
Imports AgenticCli.Agent
Imports AgenticCli.Core
Imports AgenticCli.Tools

Namespace Mcp

    ''' <summary>
    ''' agentic mcp serve: this CLI's own tools (Read, Edit, Write, Glob, Grep, LS, Bash …) as an MCP server over stdio, like
    ''' "claude mcp serve" - for Claude Desktop, Claude Code, VS Code or any other MCP client, working in the folder it starts in.
    ''' The client asks the user before its tools run; nothing here asks.
    ''' </summary>
    Friend NotInheritable Class McpServe

        Private Sub New()
        End Sub

        Public Shared Async Function RunAsync() As Task(Of Integer)
            ' stdout carries only the protocol: anything else written there goes to stderr.
            Dim protocol As New StreamWriter(Console.OpenStandardOutput(), New UTF8Encoding(False)) With {.AutoFlush = True, .NewLine = vbLf}
            Console.SetOut(Console.Error)
            Dim cwd = Environment.CurrentDirectory
            Dim state As New AgentState With {.ProjectDir = cwd, .Cwd = cwd, .Interactive = False}
            state.Settings = Settings.Load(cwd, Nothing)
            state.Shells = New ShellManager(state.Settings.Str("shell", "auto"))
            state.Skills = New Skills.SkillStore(cwd, True)
            Dim tools As New List(Of ITool) From {
                New ReadTool(), New EditTool(), New MultiEditTool(), New WriteTool(), New GlobTool(), New GrepTool(), New LsTool(),
                New BashTool(state.Shells), New BashOutputTool(state.Shells), New KillShellTool(state.Shells), New WebFetchTool()}
            Dim writeLock As New SemaphoreSlim(1, 1)
            Dim send = Async Function(msg As JsonObject) As Task
                           Await writeLock.WaitAsync().ConfigureAwait(False)
                           Try
                               Await protocol.WriteLineAsync(msg.ToJsonString()).ConfigureAwait(False)
                           Finally
                               writeLock.Release()
                           End Try
                       End Function
            Dim running As New List(Of Task)
            Using input As New StreamReader(Console.OpenStandardInput(), New UTF8Encoding(False))
                While True
                    Dim line = Await input.ReadLineAsync().ConfigureAwait(False)
                    If line Is Nothing Then Exit While
                    If line.Trim().Length = 0 Then Continue While
                    Dim msg As JsonObject
                    Try
                        msg = TryCast(JsonNode.Parse(line), JsonObject)
                    Catch ex As JsonException
                        Continue While
                    End Try
                    If msg Is Nothing OrElse msg("method") Is Nothing Then Continue While
                    Dim id = msg("id")?.DeepClone()
                    Dim method = msg("method").ToString()
                    Dim params = TryCast(msg("params"), JsonObject)
                    If id Is Nothing Then Continue While   ' notifications (initialized, cancelled …)
                    Dim reply = Function(result As JsonNode) New JsonObject From {{"jsonrpc", "2.0"}, {"id", id.DeepClone()}, {"result", result}}
                    Select Case method
                        Case "initialize"
                            Await send(reply(New JsonObject From {
                                {"protocolVersion", If(params?("protocolVersion")?.ToString(), "2025-06-18")},
                                {"capabilities", New JsonObject From {{"tools", New JsonObject()}}},
                                {"serverInfo", New JsonObject From {{"name", "agentic"}, {"version", AppInfo.Version}}},
                                {"instructions", $"The Agentic CLI's tools, working in {cwd}. Read a file before you edit it."}})).ConfigureAwait(False)
                        Case "ping"
                            Await send(reply(New JsonObject())).ConfigureAwait(False)
                        Case "tools/list"
                            Dim list As New JsonArray()
                            For Each t In tools
                                list.Add(New JsonObject From {{"name", t.Name}, {"description", t.Description}, {"inputSchema", t.Parameters()},
                                                              {"annotations", New JsonObject From {{"readOnlyHint", t.IsReadOnly}}}})
                            Next
                            Await send(reply(New JsonObject From {{"tools", list}})).ConfigureAwait(False)
                        Case "tools/call"
                            Dim name = params?("name")?.ToString()
                            Dim tool = tools.FirstOrDefault(Function(t) t.Name = name)
                            Dim argsText = If(params?("arguments")?.ToJsonString(), "{}")
                            ' Calls run side by side; each answers when it is done.
                            running.Add(Task.Run(Async Function()
                                                     Dim content As New JsonArray()
                                                     Dim isError As Boolean
                                                     If tool Is Nothing Then
                                                         content.Add(New JsonObject From {{"type", "text"}, {"text", "Unknown tool " & name}})
                                                         isError = True
                                                     Else
                                                         Try
                                                             Using doc = JsonDocument.Parse(argsText)
                                                                 Dim r = Await tool.RunAsync(doc.RootElement.Clone(), New ToolContext With {.State = state, .Cancel = CancellationToken.None}).ConfigureAwait(False)
                                                                 content.Add(New JsonObject From {{"type", "text"}, {"text", If(r.Content, "")}})
                                                                 If r.Image IsNot Nothing Then content.Add(New JsonObject From {{"type", "image"}, {"mimeType", r.ImageMime}, {"data", Convert.ToBase64String(r.Image)}})
                                                                 For Each img In If(r.Images, New List(Of (MediaType As String, Data As String)))
                                                                     content.Add(New JsonObject From {{"type", "image"}, {"mimeType", img.MediaType}, {"data", img.Data}})
                                                                 Next
                                                                 isError = Not r.Ok
                                                             End Using
                                                         Catch ex As Exception
                                                             content.Add(New JsonObject From {{"type", "text"}, {"text", ex.Message}})
                                                             isError = True
                                                         End Try
                                                     End If
                                                     Await send(reply(New JsonObject From {{"content", content}, {"isError", isError}})).ConfigureAwait(False)
                                                 End Function))
                        Case Else
                            Await send(New JsonObject From {{"jsonrpc", "2.0"}, {"id", id.DeepClone()},
                                                            {"error", New JsonObject From {{"code", -32601}, {"message", method & " is not supported"}}}}).ConfigureAwait(False)
                    End Select
                End While
            End Using
            Await Task.WhenAll(running).ConfigureAwait(False)
            Return 0
        End Function

    End Class

End Namespace
