Option Strict On
Option Explicit On
Option Infer On

Imports System.Text
Imports System.Text.Json
Imports System.Text.Json.Nodes
Imports AgenticCli.Agent
Imports AgenticCli.Core

Namespace Tools

    ''' <summary>
    ''' The resources MCP servers offer (files, records, documents), like Claude Code's ListMcpResources / ReadMcpResource:
    ''' offered when a connected server says it has resources.
    ''' </summary>
    Friend NotInheritable Class McpResourceTool
        Implements ITool

        Private ReadOnly _host As Mcp.McpHost
        Private ReadOnly _read As Boolean

        Public Sub New(host As Mcp.McpHost, read As Boolean)
            _host = host
            _read = read
        End Sub

        Public ReadOnly Property Name As String Implements ITool.Name
            Get
                Return If(_read, "ReadMcpResource", "ListMcpResources")
            End Get
        End Property

        Private Function Servers() As List(Of Mcp.McpConnection)
            Return _host.ConnectedServers().Where(Function(c) c.HasCapability("resources")).ToList()
        End Function

        Public ReadOnly Property Description As String Implements ITool.Description
            Get
                Dim ids = String.Join(", ", Servers().Select(Function(c) c.Options.Id))
                Return If(_read, "Read a resource of an MCP server by its URI (ListMcpResources shows them).",
                          $"List the resources (files, records, documents) the MCP servers offer: {ids}. Read one with ReadMcpResource.")
            End Get
        End Property

        Public ReadOnly Property IsReadOnly As Boolean = True Implements ITool.IsReadOnly

        Public Function Parameters() As JsonObject Implements ITool.Parameters
            Return Jx.ParseObject(If(_read, "{""type"":""object"",""properties"":{""server"":{""type"":""string""},""uri"":{""type"":""string""}},""required"":[""server"",""uri""]}",
                                             "{""type"":""object"",""properties"":{""server"":{""type"":""string"",""description"":""Only this server's""}}}"))
        End Function

        Public Function Title(input As JsonElement, state As AgentState) As String Implements ITool.Title
            Return Name & "(" & Jx.StrOf(input, "server", "") & If(_read, " " & Jx.StrOf(input, "uri", ""), "") & ")"
        End Function

        Public Function Preview(input As JsonElement, state As AgentState) As String Implements ITool.Preview
            Return Nothing
        End Function

        Public Async Function RunAsync(input As JsonElement, ctx As ToolContext) As Task(Of ToolResult) Implements ITool.RunAsync
            Dim server = Jx.StrOf(input, "server", "")
            Dim list = Servers()
            If _read Then
                Dim c = list.FirstOrDefault(Function(x) x.Options.Id.Equals(server, StringComparison.OrdinalIgnoreCase))
                If c Is Nothing Then Return ToolResult.Fail("No connected server called that offers resources: " & String.Join(", ", list.Select(Function(x) x.Options.Id)))
                Dim r = Await c.ReadResourceAsync(Jx.StrOf(input, "uri", "")).ConfigureAwait(False)
                If Not r.Ok Then Return ToolResult.Fail(r.Error)
                Return New ToolResult With {.Ok = True, .Content = ToolKit.Clip(r.Content, 60000), .Images = If(r.Images.Count > 0, r.Images, Nothing),
                                            .Detail = ToolKit.PreviewLines(r.Content, If(ctx.State.Verbose, 100, 4), "      ")}
            End If
            Dim sb As New StringBuilder()
            For Each c In list.Where(Function(x) server.Length = 0 OrElse x.Options.Id.Equals(server, StringComparison.OrdinalIgnoreCase))
                Try
                    For Each r In (Await c.ListAllAsync("resources/list", "resources").ConfigureAwait(False)).Take(200)
                        sb.Append(c.Options.Id).Append("  ").Append(Jx.StrOf(r, "uri", "")).Append("  ").Append(Jx.StrOf(r, "name", ""))
                        Dim d = Jx.StrOf(r, "description", "")
                        If d.Length > 0 Then sb.Append(" - ").Append(ToolKit.OneLine(d, 160))
                        sb.Append(vbLf)
                    Next
                Catch ex As Exception When TypeOf ex Is IO.IOException OrElse TypeOf ex Is InvalidOperationException OrElse TypeOf ex Is TimeoutException
                    sb.Append(c.Options.Id).Append(": ").Append(ex.Message).Append(vbLf)
                End Try
            Next
            Dim text = If(sb.Length = 0, "No resources.", sb.ToString().TrimEnd())
            Return ToolResult.Done(text, $"{text.Split(ChrW(10)).Length} resources")
        End Function

    End Class

End Namespace
