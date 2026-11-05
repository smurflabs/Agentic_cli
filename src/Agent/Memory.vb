Option Strict On
Option Explicit On
Option Infer On

Imports System.IO
Imports System.Text
Imports System.Text.RegularExpressions
Imports AgenticCli.Core

Namespace Agent

    Friend NotInheritable Class MemoryFile
        Public Property Path As String
        ''' <summary>user | project | local | parent | rules</summary>
        Public Property Scope As String
        Public Property Content As String
    End Class

    ''' <summary>
    ''' Instruction files the model always gets (Claude Code's CLAUDE.md, the AGENTS.md standard, Cline's .clinerules):
    '''   user     ~\.agentic\AGENTS.md (and ~\.claude\CLAUDE.md)
    '''   project  AGENTS.md, AGENTIC.md, CLAUDE.md, .agentic\AGENTS.md, .claude\CLAUDE.md in the project and its parent folders
    '''   local    AGENTS.local.md, CLAUDE.local.md (yours, not committed)
    '''   rules    .clinerules (file or folder), .cursorrules, .github\copilot-instructions.md in the project
    ''' Lines "@path" import another file (up to 3 levels).
    ''' </summary>
    Friend NotInheritable Class Memory

        Private Shared ReadOnly ProjectNames As String() = {"AGENTS.md", "AGENTIC.md", "CLAUDE.md", Path.Combine(".agentic", "AGENTS.md"), Path.Combine(".claude", "CLAUDE.md")}
        Private Shared ReadOnly LocalNames As String() = {"AGENTS.local.md", "CLAUDE.local.md"}

        Public Shared Function Load(projectDir As String, includeClaude As Boolean) As List(Of MemoryFile)
            Dim list As New List(Of MemoryFile)
            Dim seen As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
            Dim add = Sub(file As String, scope As String)
                          If Not IO.File.Exists(file) OrElse Not seen.Add(Path.GetFullPath(file)) Then Return
                          Dim text = Paths.ReadText(file)
                          If String.IsNullOrWhiteSpace(text) Then Return
                          list.Add(New MemoryFile With {.Path = file, .Scope = scope, .Content = ExpandImports(text, Path.GetDirectoryName(file), 0, seen)})
                      End Sub
            add(Paths.UserMemoryFile(), "user")
            If includeClaude Then add(Path.Combine(Paths.ClaudeHome(), "CLAUDE.md"), "user")
            ' Parent folders first (outermost), then the project folder.
            Dim chain As New List(Of String)
            Dim d = New DirectoryInfo(projectDir)
            While d IsNot Nothing
                chain.Add(d.FullName)
                d = d.Parent
            End While
            chain.Reverse()
            Dim home = Paths.Home()
            For Each folder In chain
                Dim isProject = folder.Equals(Path.GetFullPath(projectDir).TrimEnd("\"c), StringComparison.OrdinalIgnoreCase) OrElse folder.Equals(Path.GetFullPath(projectDir), StringComparison.OrdinalIgnoreCase)
                ' The home folder itself is the user scope, not a parent project.
                If Not isProject AndAlso folder.Equals(home, StringComparison.OrdinalIgnoreCase) Then Continue For
                For Each n In ProjectNames
                    If Not includeClaude AndAlso n.Contains("CLAUDE") Then Continue For
                    add(Path.Combine(folder, n), If(isProject, "project", "parent"))
                Next
                For Each n In LocalNames
                    add(Path.Combine(folder, n), "local")
                Next
            Next
            ' Other tools' rules in the project.
            Dim cline = Path.Combine(projectDir, ".clinerules")
            If File.Exists(cline) Then
                add(cline, "rules")
            ElseIf Directory.Exists(cline) Then
                For Each f In Directory.EnumerateFiles(cline, "*.md").OrderBy(Function(x) x, StringComparer.OrdinalIgnoreCase)
                    add(f, "rules")
                Next
            End If
            add(Path.Combine(projectDir, ".cursorrules"), "rules")
            add(Path.Combine(projectDir, ".github", "copilot-instructions.md"), "rules")
            Return list
        End Function

        Private Shared Function ExpandImports(text As String, baseDir As String, depth As Integer, seen As HashSet(Of String)) As String
            If depth >= 3 Then Return text
            Dim inFence = False
            Dim sb As New StringBuilder()
            For Each line In text.Replace(ChrW(13), "").Split(ChrW(10))
                If line.TrimStart().StartsWith("```") Then inFence = Not inFence
                Dim m = Regex.Match(line, "^\s*@([^\s]+)\s*$")
                If Not inFence AndAlso m.Success Then
                    Dim target = Paths.Full(m.Groups(1).Value, baseDir)
                    If File.Exists(target) AndAlso seen.Add(target) Then
                        sb.AppendLine($"<!-- imported from {m.Groups(1).Value} -->")
                        sb.AppendLine(ExpandImports(If(Paths.ReadText(target), ""), Path.GetDirectoryName(target), depth + 1, seen))
                        Continue For
                    End If
                End If
                sb.AppendLine(line)
            Next
            Return sb.ToString().TrimEnd()
        End Function

        ''' <summary>All memory as one block for the instructions (each file cut at 20k, all at 60k characters).</summary>
        Public Shared Function Combined(files As List(Of MemoryFile)) As String
            Dim sb As New StringBuilder()
            For Each f In files
                Dim body = f.Content
                If body.Length > 20000 Then body = body.Substring(0, 20000) & vbLf & "… (cut)"
                sb.AppendLine($"Contents of {f.Path} ({DescribeScope(f.Scope)}):").AppendLine().AppendLine(body).AppendLine()
                If sb.Length > 60000 Then Exit For
            Next
            Return sb.ToString().Trim()
        End Function

        Public Shared Function DescribeScope(scope As String) As String
            Select Case scope
                Case "user" : Return "the user's private global instructions for all projects"
                Case "project" : Return "project instructions, checked into the codebase"
                Case "local" : Return "the user's private project instructions, not checked in"
                Case "parent" : Return "instructions from a parent folder"
                Case Else : Return "project rules"
            End Select
        End Function

        ''' <summary>"# remember this" adds a bullet to a memory file (creating it).</summary>
        Public Shared Sub Append(file As String, text As String)
            Dim existing = If(Paths.ReadText(file), "")
            Dim sep = If(existing.Length = 0, "", If(existing.EndsWith(vbLf), "", Environment.NewLine))
            Dim bullet = "- " & text.Trim()
            Paths.WriteText(file, existing & sep & bullet & Environment.NewLine)
        End Sub

    End Class

End Namespace
