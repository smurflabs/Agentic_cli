Option Strict On
Option Explicit On
Option Infer On

Imports System.IO
Imports AgenticCli.Core

Namespace Agent

    Friend NotInheritable Class SubagentDef
        Public Property Name As String
        Public Property Description As String
        ''' <summary>Tool names it may use; Nothing for all (except Task).</summary>
        Public Property Tools As List(Of String)
        ''' <summary>A model id, or Nothing / "inherit" for the session's model.</summary>
        Public Property Model As String
        Public Property Prompt As String
        Public Property Source As String
        Public Property File As String
    End Class

    ''' <summary>
    ''' Subagent types for the Task tool: built-in (general-purpose, Explore, Plan) and your own, as Markdown files with front
    ''' matter (name, description, tools, model) in .agentic\agents, ~\.agentic\agents, .claude\agents or ~\.claude\agents.
    ''' </summary>
    Friend NotInheritable Class Subagents

        Private Shared ReadOnly ReadOnlyTools As New List(Of String) From {"Read", "Glob", "Grep", "LS", "WebFetch", "Bash", "BashOutput", "Skill", "SkillFile"}

        Public Shared Function BuiltIn() As List(Of SubagentDef)
            Return New List(Of SubagentDef) From {
                New SubagentDef With {.Name = "general-purpose", .Source = "built-in",
                    .Description = "General-purpose agent for researching complex questions, searching for code, and executing multi-step tasks. When you are searching for a keyword or file and are not confident that you will find the right match in the first few tries, use this agent.",
                    .Prompt = "You are a subagent of Agentic CLI. Do the task you are given completely, using the tools, then answer with a concise report of what you found or did (file paths with line numbers, key findings). The report goes to the main agent, not to the user."},
                New SubagentDef With {.Name = "Explore", .Source = "built-in", .Tools = ReadOnlyTools,
                    .Description = "Fast agent specialized for exploring codebases: finding files by patterns, searching code for keywords, answering questions about how the codebase works. Say how thorough to be: quick, medium or very thorough.",
                    .Prompt = "You are a read-only codebase explorer for Agentic CLI. Search efficiently with Glob, Grep and Read (Bash only for read-only commands such as git log). Never create, change or delete files. Answer with a concise report: the relevant files (with line numbers) and how things fit together."},
                New SubagentDef With {.Name = "Plan", .Source = "built-in", .Tools = ReadOnlyTools,
                    .Description = "Research agent for planning: looks through the codebase and gathers what is needed to plan an implementation, without changing anything.",
                    .Prompt = "You research a codebase to prepare an implementation plan for Agentic CLI. Read and search only; never change files. Answer with the relevant files, the existing patterns to follow, risks, and a step-by-step plan."}}
        End Function

        Public Shared Function All(projectDir As String, includeClaude As Boolean) As List(Of SubagentDef)
            Dim list = BuiltIn()
            Dim folders As New List(Of (Dir As String, Source As String)) From {
                (Path.Combine(projectDir, ".agentic", "agents"), "project"), (Path.Combine(Paths.AgenticHome(), "agents"), "user")}
            If includeClaude Then
                folders.Insert(1, (Path.Combine(projectDir, ".claude", "agents"), "project (.claude)"))
                folders.Add((Path.Combine(Paths.ClaudeHome(), "agents"), "Claude Code"))
            End If
            ' A Claude Code plugin's agents (/plugin) are named plugin:agent.
            For Each p In Core.Plugins.Enabled({projectDir}).Where(Function(x) includeClaude OrElse x.Source = "Agentic")
                For Each d In Core.Plugins.Folders(p.Dir, "agents")
                    folders.Add((d, "plugin:" & p.Name))
                Next
            Next
            For Each f In folders
                If Not Directory.Exists(f.Dir) Then Continue For
                Try
                    For Each md In Directory.EnumerateFiles(f.Dir, "*.md")
                        Dim parsed = Skills.YamlFrontMatter.Split(Paths.ReadText(md))
                        Dim name = FrontStr(parsed.Front, "name", Path.GetFileNameWithoutExtension(md))
                        If f.Source.StartsWith("plugin:", StringComparison.Ordinal) Then name = f.Source.Substring(7) & ":" & name
                        If list.Any(Function(a) a.Name.Equals(name, StringComparison.OrdinalIgnoreCase) AndAlso a.Source <> "built-in") Then Continue For
                        list.RemoveAll(Function(a) a.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                        Dim toolsText = FrontStr(parsed.Front, "tools", Nothing)
                        list.Add(New SubagentDef With {
                            .Name = name, .Description = FrontStr(parsed.Front, "description", ""), .Model = FrontStr(parsed.Front, "model", Nothing),
                            .Tools = If(toolsText Is Nothing, Nothing, toolsText.Split(","c).Select(Function(t) t.Trim()).Where(Function(t) t.Length > 0).ToList()),
                            .Prompt = parsed.Body, .Source = f.Source, .File = md})
                    Next
                Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
                End Try
            Next
            Return list
        End Function

        Private Shared Function FrontStr(map As Dictionary(Of String, Object), key As String, fallback As String) As String
            Dim o As Object = Nothing
            If map.TryGetValue(key, o) AndAlso o IsNot Nothing Then
                Dim v = If(TypeOf o Is List(Of String), String.Join(", ", DirectCast(o, List(Of String))), o.ToString())
                If v.Trim().Length > 0 Then Return v.Trim()
            End If
            Return fallback
        End Function

        ''' <summary>A new agent file from a template; returns its path.</summary>
        Public Shared Function Create(projectDir As String, scope As String, name As String, description As String, tools As String, prompt As String) As String
            Dim slug = New String(name.ToLowerInvariant().Select(Function(c) If(Char.IsLetterOrDigit(c) OrElse c = "-"c, c, "-"c)).ToArray()).Trim("-"c)
            Dim dir = If(scope = "project", Path.Combine(projectDir, ".agentic", "agents"), Path.Combine(Paths.AgenticHome(), "agents"))
            Dim file = Path.Combine(dir, slug & ".md")
            Dim front = "---" & vbLf & "name: " & slug & vbLf & "description: " & description & vbLf & If(String.IsNullOrWhiteSpace(tools), "", "tools: " & tools & vbLf) & "model: inherit" & vbLf & "---" & vbLf & vbLf
            Paths.WriteText(file, front & prompt.Trim() & vbLf)
            Return file
        End Function

    End Class

End Namespace
