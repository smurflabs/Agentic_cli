Option Strict On
Option Explicit On
Option Infer On

Imports System.Text
Imports System.Text.Json
Imports System.Text.Json.Nodes
Imports AgenticCli.Agent
Imports AgenticCli.Core
Imports AgenticCli.Ui

Namespace Tools

    ''' <summary>TodoWrite: the task list the model keeps for multi-step work (shown in the transcript, Ctrl+T, /todos).</summary>
    Friend NotInheritable Class TodoWriteTool
        Implements ITool

        Public ReadOnly Property Name As String = "TodoWrite" Implements ITool.Name
        Public ReadOnly Property Description As String = "Creates and updates your task list for the current work: give the whole list each time. " &
            "Use it for tasks with 3 or more steps or when the user gives several tasks; skip it for single, trivial requests. " &
            "Each item has content (imperative: ""Run the tests""), activeForm (continuous: ""Running the tests"") and status pending | in_progress | completed. " &
            "Keep exactly one item in_progress while working, mark items completed as soon as they are done (not in batches), and add items you discover." Implements ITool.Description
        Public ReadOnly Property IsReadOnly As Boolean = True Implements ITool.IsReadOnly

        Public Function Parameters() As JsonObject Implements ITool.Parameters
            Dim item As New JsonObject From {{"type", "object"}, {"properties", New JsonObject From {
                {"content", New JsonObject From {{"type", "string"}}},
                {"status", New JsonObject From {{"type", "string"}, {"enum", New JsonArray("pending", "in_progress", "completed")}}},
                {"activeForm", New JsonObject From {{"type", "string"}}}}}, {"required", New JsonArray("content", "status")}}
            Return New JsonObject From {{"type", "object"}, {"properties", New JsonObject From {{"todos", New JsonObject From {{"type", "array"}, {"items", item}}}}},
                                        {"required", New JsonArray("todos")}}
        End Function

        Public Function Title(input As JsonElement, state As AgentState) As String Implements ITool.Title
            Return "Update Todos"
        End Function

        Public Function Preview(input As JsonElement, state As AgentState) As String Implements ITool.Preview
            Return Nothing
        End Function

        Public Function RunAsync(input As JsonElement, ctx As ToolContext) As Task(Of ToolResult) Implements ITool.RunAsync
            Dim list As New List(Of TodoItem)
            For Each t In Jx.ArrOf(input, "todos")
                Dim status = Jx.StrOf(t, "status", "pending").ToLowerInvariant()
                If status <> "pending" AndAlso status <> "in_progress" AndAlso status <> "completed" Then status = "pending"
                Dim content = Jx.StrOf(t, "content", "").Trim()
                If content.Length = 0 Then Continue For
                list.Add(New TodoItem With {.Content = content, .Status = status, .ActiveForm = Jx.StrOf(t, "activeForm", content)})
            Next
            ctx.State.Todos = list
            Dim content2 = "Todos have been modified successfully. Ensure that you continue to use the todo list to track your progress. Please proceed with the current tasks if applicable."
            Return Task.FromResult(ToolResult.Done(content2, Nothing, Render(list, "      ")))
        End Function

        ''' <summary>☒ done (struck through), ☐ in progress (bold), ☐ pending.</summary>
        Public Shared Function Render(list As List(Of TodoItem), indent As String) As String
            If list.Count = 0 Then Return indent & Ansi.Muted("(no todos)") & Environment.NewLine
            Dim sb As New StringBuilder()
            For Each t In list
                Select Case t.Status
                    Case "completed" : sb.Append(indent).Append(Ansi.Ok(Ansi.CheckOn)).Append(" ").Append(Ansi.Muted(Ansi.Strike(t.Content)))
                    Case "in_progress" : sb.Append(indent).Append(Ansi.Accent(Ansi.CheckOff)).Append(" ").Append(Ansi.Bold(Ansi.Accent(t.Content)))
                    Case Else : sb.Append(indent).Append(Ansi.Muted(Ansi.CheckOff)).Append(" ").Append(t.Content)
                End Select
                sb.Append(Environment.NewLine)
            Next
            Return sb.ToString()
        End Function

    End Class

    ''' <summary>Task: hands a self-contained job to a subagent with its own conversation and tools; returns its report.</summary>
    Friend NotInheritable Class TaskTool
        Implements ITool

        Private ReadOnly _agents As Func(Of String)
        ''' <summary>How deep subagents may start their own: the main conversation is 0, so 3 allows two more levels.</summary>
        Public Const MaxDepth As Integer = 3

        ''' <param name="agentList">Lines describing the available subagent types.</param>
        Public Sub New(agentList As Func(Of String))
            _agents = agentList
        End Sub

        Public ReadOnly Property Name As String = "Task" Implements ITool.Name
        Public ReadOnly Property IsReadOnly As Boolean = True Implements ITool.IsReadOnly

        Public ReadOnly Property Description As String Implements ITool.Description
            Get
                Return "Launches a subagent to handle a complex, multi-step job on its own (searching a large codebase, researching a question, carrying out a well-defined change). " &
                    "It has its own context and tools and returns one final report, which the user does not see: summarise what matters for them. " &
                    "Give a complete, self-contained prompt (the subagent knows nothing of this conversation, unless you set fork to true: then it starts with the whole " &
                    "conversation so far, for side tasks that need its context) and say exactly what it should return. " &
                    "Subagents launched in the SAME response run AT THE SAME TIME: for independent parts of a job, call Task several times in one response. " &
                    "Do not use it to read one known file or to search for one specific symbol (use Read, Glob, Grep). Available subagent types:" & vbLf & _agents()
            End Get
        End Property

        Public Function Parameters() As JsonObject Implements ITool.Parameters
            Return ToolKit.Schema({"description", "prompt", "subagent_type"}, ("description", "string", "A short (3-5 word) description of the job"),
                                  ("prompt", "string", "The job for the subagent, complete and self-contained"), ("subagent_type", "string", "Which subagent to use"),
                                  ("fork", "boolean", "true: the subagent starts with this whole conversation (its context); default false: only your prompt"))
        End Function

        Public Function Title(input As JsonElement, state As AgentState) As String Implements ITool.Title
            Return "Task(" & ToolKit.OneLine(ToolKit.Arg(input, "description", ToolKit.Arg(input, "subagent_type", "")), 70) & ")"
        End Function

        Public Function Preview(input As JsonElement, state As AgentState) As String Implements ITool.Preview
            Return "      " & Ansi.Muted(ToolKit.OneLine(ToolKit.Arg(input, "prompt", ""), 300)) & Environment.NewLine
        End Function

        Public Async Function RunAsync(input As JsonElement, ctx As ToolContext) As Task(Of ToolResult) Implements ITool.RunAsync
            If ctx.RunSubagent Is Nothing Then Return ToolResult.Fail("Subagents are not available here.")
            If ctx.Depth >= MaxDepth Then Return ToolResult.Fail($"Subagents can start their own only {MaxDepth - 1} levels deep: do this part yourself.")
            Dim prompt = ToolKit.Arg(input, "prompt")
            If String.IsNullOrWhiteSpace(prompt) Then Return ToolResult.Fail("prompt is required.")
            ctx.Fork = ToolKit.ArgBool(input, "fork", False)
            Return Await ctx.RunSubagent.Invoke(ToolKit.Arg(input, "subagent_type", "general-purpose"), ToolKit.Arg(input, "description", "subagent"), prompt, ctx).ConfigureAwait(False)
        End Function

    End Class

    ''' <summary>ExitPlanMode: in plan mode, presents the plan for approval.</summary>
    Friend NotInheritable Class ExitPlanModeTool
        Implements ITool

        Public ReadOnly Property Name As String = "ExitPlanMode" Implements ITool.Name
        Public ReadOnly Property Description As String = "In plan mode, when your plan to implement the task is ready, call this with the plan (Markdown, concise) to ask the user to approve it. " &
            "Only for tasks that need code changes, not for research or questions. Do not use it before you have looked at the relevant code." Implements ITool.Description
        Public ReadOnly Property IsReadOnly As Boolean = True Implements ITool.IsReadOnly

        Public Function Parameters() As JsonObject Implements ITool.Parameters
            Return ToolKit.Schema({"plan"}, ("plan", "string", "The plan, in Markdown"))
        End Function

        Public Function Title(input As JsonElement, state As AgentState) As String Implements ITool.Title
            Return "Ready to code?"
        End Function

        Public Function Preview(input As JsonElement, state As AgentState) As String Implements ITool.Preview
            Return Nothing
        End Function

        Public Function RunAsync(input As JsonElement, ctx As ToolContext) As Task(Of ToolResult) Implements ITool.RunAsync
            Dim plan = ToolKit.Arg(input, "plan", "")
            If ctx.ApprovePlan Is Nothing Then Return Task.FromResult(ToolResult.Fail("The plan cannot be approved here (no terminal). Present it as your answer instead."))
            Dim answer = ctx.ApprovePlan.Invoke(plan)
            If answer.Choice = "auto" OrElse answer.Choice = "manual" Then
                ctx.State.Mode = If(answer.Choice = "auto", PermissionMode.AcceptEdits, PermissionMode.Default)
                ctx.State.PendingFollowUp = "The plan is approved. Implement it now." & If(String.IsNullOrWhiteSpace(answer.Feedback), "", " Note from the user: " & answer.Feedback)
                Return Task.FromResult(ToolResult.Done("The user approved the plan. Plan mode has ended. Stop here without further tool calls: you will be asked to implement the plan next, with all tools.",
                                                       Ansi.Ok("User approved the plan")))
            End If
            Return Task.FromResult(ToolResult.Fail("The user wants to keep planning and did not approve the plan." &
                                                   If(String.IsNullOrWhiteSpace(answer.Feedback), " Ask what they would like to change.", " Their feedback: " & answer.Feedback),
                                                   "User rejected the plan"))
        End Function

    End Class

    ''' <summary>Skill: loads a skill's instructions (Claude Code skills) when a request matches its description.</summary>
    Friend NotInheritable Class SkillTool
        Implements ITool

        Public ReadOnly Property Name As String = "Skill" Implements ITool.Name
        Public ReadOnly Property Description As String = "Loads a skill: its full instructions and the list of its files. Use it when the user's request matches a skill's description " &
            "(listed in your instructions), then follow the instructions. Read the skill's files with SkillFile." Implements ITool.Description
        Public ReadOnly Property IsReadOnly As Boolean = True Implements ITool.IsReadOnly

        Public Function Parameters() As JsonObject Implements ITool.Parameters
            Return ToolKit.Schema({"skill"}, ("skill", "string", "The skill's name"), ("args", "string", "Arguments, for a command-style skill"))
        End Function

        Public Function Title(input As JsonElement, state As AgentState) As String Implements ITool.Title
            Return "Skill(" & ToolKit.Arg(input, "skill", ToolKit.Arg(input, "name", "")) & ")"
        End Function

        Public Function Preview(input As JsonElement, state As AgentState) As String Implements ITool.Preview
            Return Nothing
        End Function

        Public Function RunAsync(input As JsonElement, ctx As ToolContext) As Task(Of ToolResult) Implements ITool.RunAsync
            Dim nameArg = ToolKit.Arg(input, "skill", ToolKit.Arg(input, "name", ""))
            Dim s = ctx.State.Skills?.FindByName(nameArg)
            If s Is Nothing Then Return Task.FromResult(ToolResult.Fail($"No skill named '{nameArg}'."))
            Dim body = If(s.Kind = "command", Skills.SkillStore.Expand(s, ToolKit.Arg(input, "args", "")), s.Body)
            Dim files = If(s.Files.Count = 0, "", vbLf & vbLf & "Files in the skill (read them with SkillFile): " & String.Join(", ", s.Files.Take(100).Select(Function(f) f.Path)))
            Dim content = $"<skill name=""{s.Name}"" location=""{s.Location}"">" & vbLf & body & files & vbLf & "</skill>"
            Return Task.FromResult(ToolResult.Done(content, "Loaded skill " & Ansi.Bold(s.Name)))
        End Function

    End Class

    Friend NotInheritable Class SkillFileTool
        Implements ITool

        Public ReadOnly Property Name As String = "SkillFile" Implements ITool.Name
        Public ReadOnly Property Description As String = "Reads a file that belongs to a skill (scripts, references, templates), by the skill's name and the file's relative path." Implements ITool.Description
        Public ReadOnly Property IsReadOnly As Boolean = True Implements ITool.IsReadOnly

        Public Function Parameters() As JsonObject Implements ITool.Parameters
            Return ToolKit.Schema({"skill", "path"}, ("skill", "string", "The skill's name"), ("path", "string", "The file's path inside the skill"))
        End Function

        Public Function Title(input As JsonElement, state As AgentState) As String Implements ITool.Title
            Return "SkillFile(" & ToolKit.Arg(input, "skill", "") & "/" & ToolKit.Arg(input, "path", "") & ")"
        End Function

        Public Function Preview(input As JsonElement, state As AgentState) As String Implements ITool.Preview
            Return Nothing
        End Function

        Public Function RunAsync(input As JsonElement, ctx As ToolContext) As Task(Of ToolResult) Implements ITool.RunAsync
            Dim s = ctx.State.Skills?.FindByName(ToolKit.Arg(input, "skill", ""))
            If s Is Nothing Then Return Task.FromResult(ToolResult.Fail("No such skill."))
            Dim text = Skills.SkillStore.ReadSkillFile(s, ToolKit.Arg(input, "path", ""))
            If text Is Nothing Then Return Task.FromResult(ToolResult.Fail("No such file in the skill. Files: " & String.Join(", ", s.Files.Take(60).Select(Function(f) f.Path))))
            Return Task.FromResult(ToolResult.Done(ToolKit.Clip(text, 60000), $"Read {text.Split(ChrW(10)).Length} lines"))
        End Function

    End Class

    ''' <summary>A tool of a local MCP server, offered to the model as mcp__server__tool.</summary>
    Friend NotInheritable Class McpTool
        Implements ITool

        Private ReadOnly _server As String
        Private ReadOnly _tool As Mcp.McpToolInfo
        Private ReadOnly _host As Mcp.McpHost

        Public Sub New(server As String, tool As Mcp.McpToolInfo, host As Mcp.McpHost)
            _server = server
            _tool = tool
            _host = host
            Name = ToolName(server, tool.Name)
        End Sub

        Public Shared Function ToolName(server As String, tool As String) As String
            Dim raw = "mcp__" & server & "__" & tool
            Dim clean = New String(raw.Select(Function(c) If(Char.IsLetterOrDigit(c) OrElse c = "_"c OrElse c = "-"c, c, "_"c)).ToArray())
            Return If(clean.Length > 64, clean.Substring(0, 64), clean)
        End Function

        Public ReadOnly Property Name As String Implements ITool.Name
        Public ReadOnly Property Server As String
            Get
                Return _server
            End Get
        End Property

        Public ReadOnly Property Description As String Implements ITool.Description
            Get
                Return $"[MCP server {_server}] " & If(_tool.Description, "")
            End Get
        End Property

        Public ReadOnly Property IsReadOnly As Boolean = False Implements ITool.IsReadOnly

        Public Function Parameters() As JsonObject Implements ITool.Parameters
            Dim schema = Jx.ParseObject(If(_tool.InputSchema, "{}"))
            If Not schema.ContainsKey("type") Then schema("type") = "object"
            If Not schema.ContainsKey("properties") Then schema("properties") = New JsonObject()
            schema.Remove("$schema")
            Return schema
        End Function

        Public Function Title(input As JsonElement, state As AgentState) As String Implements ITool.Title
            Dim args = If(input.ValueKind = JsonValueKind.Object, input.GetRawText(), "")
            Return $"{_server} - {_tool.Name} (MCP)" & If(args.Length > 2, "(" & ToolKit.OneLine(args, 80) & ")", "")
        End Function

        Public Function Preview(input As JsonElement, state As AgentState) As String Implements ITool.Preview
            If input.ValueKind <> JsonValueKind.Object Then Return Nothing
            Return ToolKit.PreviewLines(Jx.Stringify(input, True), 16, "      ")
        End Function

        Public Async Function RunAsync(input As JsonElement, ctx As ToolContext) As Task(Of ToolResult) Implements ITool.RunAsync
            Dim r = Await _host.CallToolAsync(_server, _tool.Name, If(input.ValueKind = JsonValueKind.Object, input.GetRawText(), "{}")).ConfigureAwait(False)
            If Not r.Ok Then Return ToolResult.Fail(If(r.Error, If(r.Content, "The MCP tool failed.")), "Error: " & ToolKit.OneLine(If(r.Error, r.Content), 120))
            Return New ToolResult With {.Ok = True, .Content = ToolKit.Clip(If(r.Content, ""), 40000), .Summary = If(r.Images.Count > 0, $"{r.Images.Count} picture{If(r.Images.Count = 1, "", "s")} for the model", Nothing),
                                        .Detail = ToolKit.PreviewLines(If(r.Content, ""), If(ctx.State.Verbose, 100, 4), "      "), .Images = If(r.Images.Count > 0, r.Images, Nothing)}
        End Function

    End Class

End Namespace
