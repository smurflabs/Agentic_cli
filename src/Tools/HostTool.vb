Option Strict On
Option Explicit On
Option Infer On

Imports System.Text.Json
Imports System.Text.Json.Nodes
Imports System.Threading
Imports AgenticCli.Agent
Imports AgenticCli.Core

Namespace Tools

    ''' <summary>
    ''' A tool registered by the program that drives the CLI (stream-json input, "register_tools"). When the model calls it, the
    ''' CLI sends a host_tool_call event and waits for the host's tool_result.
    ''' </summary>
    Friend NotInheritable Class HostTool
        Implements ITool

        Private ReadOnly _schema As JsonObject
        Private ReadOnly _invoke As Func(Of String, JsonElement, CancellationToken, Task(Of ToolResult))

        Public Sub New(name As String, description As String, schema As JsonObject, isReadOnly As Boolean, invoke As Func(Of String, JsonElement, CancellationToken, Task(Of ToolResult)))
            Me.Name = name
            Me.Description = description
            Me.IsReadOnly = isReadOnly
            _schema = If(schema, New JsonObject From {{"type", "object"}, {"properties", New JsonObject()}})
            If Not _schema.ContainsKey("type") Then _schema("type") = "object"
            _invoke = invoke
        End Sub

        Public ReadOnly Property Name As String Implements ITool.Name
        Public ReadOnly Property Description As String Implements ITool.Description
        Public ReadOnly Property IsReadOnly As Boolean Implements ITool.IsReadOnly

        Public Function Parameters() As JsonObject Implements ITool.Parameters
            Return DirectCast(_schema.DeepClone(), JsonObject)
        End Function

        Public Function Title(input As JsonElement, state As AgentState) As String Implements ITool.Title
            Return Name & "(" & ToolKit.OneLine(If(input.ValueKind = JsonValueKind.Object, input.GetRawText(), ""), 80) & ")"
        End Function

        Public Function Preview(input As JsonElement, state As AgentState) As String Implements ITool.Preview
            Return If(input.ValueKind = JsonValueKind.Object, ToolKit.PreviewLines(Jx.Stringify(input, True), 12, "      "), Nothing)
        End Function

        Public Function RunAsync(input As JsonElement, ctx As ToolContext) As Task(Of ToolResult) Implements ITool.RunAsync
            Return _invoke(Name, input, ctx.Cancel)
        End Function

    End Class

End Namespace
