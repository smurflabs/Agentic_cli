Option Strict On
Option Explicit On
Option Infer On

Imports System.Text.Json
Imports System.Text.Json.Nodes
Imports System.Threading
Imports AgenticCli.Agent
Imports AgenticCli.Core

Namespace Tools

    ''' <summary>A tool the model can call; it runs here, on the user's computer.</summary>
    Friend Interface ITool
        ReadOnly Property Name As String
        ReadOnly Property Description As String
        ''' <summary>JSON schema of the arguments.</summary>
        Function Parameters() As JsonObject
        ''' <summary>Only reads (never changes files or runs programs).</summary>
        ReadOnly Property IsReadOnly As Boolean
        ''' <summary>What the transcript shows: "Read(src/app.vb)".</summary>
        Function Title(input As JsonElement, state As AgentState) As String
        ''' <summary>What the permission question shows before it runs (a diff, the command); Nothing for none.</summary>
        Function Preview(input As JsonElement, state As AgentState) As String
        Function RunAsync(input As JsonElement, ctx As ToolContext) As Task(Of ToolResult)
    End Interface

    Friend NotInheritable Class ToolContext
        Public Property State As AgentState
        Public Property Cancel As CancellationToken
        ''' <summary>0 for the main conversation, 1 inside a subagent.</summary>
        Public Property Depth As Integer
        ''' <summary>Runs a subagent (Task tool): type, description, prompt, this context → its final report.</summary>
        Public Property RunSubagent As Func(Of String, String, String, ToolContext, Task(Of ToolResult))
        ''' <summary>The conversation and the call this tool runs for (a subagent streams into that call's card in the web app).</summary>
        Public Property ChatId As String
        Public Property CallId As String
        ''' <summary>Task: the subagent starts with this conversation (a fork) instead of only its prompt.</summary>
        Public Property Fork As Boolean
        ''' <summary>Shows a plan (ExitPlanMode) and returns the user's choice: "auto", "manual" or "" (keep planning) with feedback.</summary>
        Public Property ApprovePlan As Func(Of String, (Choice As String, Feedback As String))
    End Class

    Friend NotInheritable Class ToolResult
        Public Property Ok As Boolean = True
        ''' <summary>What the model receives.</summary>
        Public Property Content As String = ""
        ''' <summary>The ⎿ line in the transcript.</summary>
        Public Property Summary As String
        ''' <summary>Extra lines for the transcript (a diff, the start of the output), already styled.</summary>
        Public Property Detail As String
        ''' <summary>A picture the model gets with the result (Read of an image): image/png, image/jpeg or image/webp.</summary>
        Public Property ImageMime As String
        Public Property Image As Byte()
        Public Property ImageName As String
        ''' <summary>Pictures an MCP tool returned (a browser screenshot): media type and base64; shown with the answer too.</summary>
        Public Property Images As List(Of (MediaType As String, Data As String))

        Public Shared Function Fail(message As String, Optional summary As String = Nothing) As ToolResult
            Return New ToolResult With {.Ok = False, .Content = message, .Summary = If(summary, message)}
        End Function

        Public Shared Function Done(content As String, Optional summary As String = Nothing, Optional detail As String = Nothing) As ToolResult
            Return New ToolResult With {.Ok = True, .Content = content, .Summary = summary, .Detail = detail}
        End Function
    End Class

    ''' <summary>Schema building and argument reading shared by the tools.</summary>
    Friend NotInheritable Class ToolKit

        Public Shared Function Schema(required As String(), ParamArray props As (Name As String, Type As String, Description As String)()) As JsonObject
            Dim p As New JsonObject()
            For Each x In props
                Dim def As New JsonObject From {{"type", x.Type}, {"description", x.Description}}
                If x.Type = "array" Then def("items") = New JsonObject From {{"type", "string"}}
                p(x.Name) = def
            Next
            Dim o As New JsonObject From {{"type", "object"}, {"properties", p}}
            If required IsNot Nothing AndAlso required.Length > 0 Then o("required") = New JsonArray(required.Select(Function(r) CType(JsonValue.Create(r), JsonNode)).ToArray())
            Return o
        End Function

        Public Shared Function Arg(input As JsonElement, name As String, Optional fallback As String = Nothing) As String
            Dim v = Jx.StrOf(input, name, Nothing)
            Return If(String.IsNullOrEmpty(v), fallback, v)
        End Function

        Public Shared Function ArgInt(input As JsonElement, name As String, fallback As Integer) As Integer
            Return Jx.IntOf(input, name, fallback)
        End Function

        Public Shared Function ArgBool(input As JsonElement, name As String, fallback As Boolean) As Boolean
            Return Jx.BoolOf(input, name, fallback)
        End Function

        ''' <summary>One line of an argument for titles: newlines folded, cut to a length.</summary>
        Public Shared Function OneLine(s As String, Optional max As Integer = 80) As String
            If String.IsNullOrEmpty(s) Then Return ""
            Dim one = s.Replace(ChrW(13), "").Replace(ChrW(10), " ⏎ ").Trim()
            Return If(one.Length > max, one.Substring(0, max - 1) & "…", one)
        End Function

        ''' <summary>Very long tool output: the start and the end are kept, the middle is cut.</summary>
        Public Shared Function Clip(text As String, Optional maxChars As Integer = 30000) As String
            If text Is Nothing OrElse text.Length <= maxChars Then Return If(text, "")
            Dim head = maxChars * 2 \ 3
            Dim tail = maxChars - head
            Return text.Substring(0, head) & $"{vbLf}{vbLf}… [{text.Length - maxChars:N0} characters cut] …{vbLf}{vbLf}" & text.Substring(text.Length - tail)
        End Function

        ''' <summary>The first lines of output for the transcript, with "… +N lines" when there is more.</summary>
        Public Shared Function PreviewLines(text As String, maxLines As Integer, indent As String) As String
            If String.IsNullOrEmpty(text) Then Return Nothing
            Dim lines = text.Replace(ChrW(13), "").TrimEnd(ChrW(10)).Split(ChrW(10))
            Dim sb As New Text.StringBuilder()
            Dim w = Ui.Ansi.TermWidth() - Ui.Ansi.Width(indent) - 2
            For Each l In lines.Take(maxLines)
                sb.Append(indent).Append(Ui.Ansi.Truncate(l.Replace(vbTab, "    "), w)).Append(Environment.NewLine)
            Next
            If lines.Length > maxLines Then sb.Append(indent).Append(Ui.Ansi.Muted($"… +{lines.Length - maxLines} lines ({Ui.KeyBindings.ShortLabel("verbose")} to show all)")).Append(Environment.NewLine)
            Return sb.ToString()
        End Function

    End Class

End Namespace
