Option Strict On
Option Explicit On
Option Infer On

Imports System.IO
Imports System.Net.Http
Imports System.Text
Imports System.Text.Json
Imports System.Text.Json.Nodes
Imports AgenticCli.Agent
Imports AgenticCli.Core

Namespace Tools

    ''' <summary>IdeDiagnostics: the problems the connected editor (/ide) reports, like Claude Code's getDiagnostics.</summary>
    Friend NotInheritable Class IdeDiagnosticsTool
        Implements ITool

        Public ReadOnly Property Name As String = "IdeDiagnostics" Implements ITool.Name
        Public ReadOnly Property Description As String = "The problems (errors and warnings from the language servers, linters and compilers) that the user's " &
            "connected editor (VS Code) reports, for one file or the whole workspace: file, line, severity and message. Use it after changing code to check " &
            "that you did not add problems, and to find what to fix." Implements ITool.Description
        Public ReadOnly Property IsReadOnly As Boolean = True Implements ITool.IsReadOnly

        Public Function Parameters() As JsonObject Implements ITool.Parameters
            Return ToolKit.Schema(Nothing, ("path", "string", "One file (default: every file with problems)"))
        End Function

        Public Function Title(input As JsonElement, state As AgentState) As String Implements ITool.Title
            Dim p = ToolKit.Arg(input, "path", "")
            Return "Diagnostics(" & If(p.Length = 0, "workspace", state.Display(state.Resolve(p))) & ")"
        End Function

        Public Function Preview(input As JsonElement, state As AgentState) As String Implements ITool.Preview
            Return Nothing
        End Function

        Public Async Function RunAsync(input As JsonElement, ctx As ToolContext) As Task(Of ToolResult) Implements ITool.RunAsync
            Dim ide = ctx.State.Ide
            If ide Is Nothing Then Return ToolResult.Fail("No editor is connected. The user can connect VS Code with /ide.")
            Dim p = ToolKit.Arg(input, "path", "")
            Dim list As JsonElement
            Try
                list = Await ide.DiagnosticsAsync(If(p.Length = 0, Nothing, ctx.State.Resolve(p)), ctx.Cancel).ConfigureAwait(False)
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is HttpRequestException OrElse TypeOf ex Is OperationCanceledException
                Return ToolResult.Fail($"{ide.Info.IdeName} did not answer: {ex.Message}")
            End Try
            If list.ValueKind <> JsonValueKind.Array OrElse list.GetArrayLength() = 0 Then Return ToolResult.Done($"{ide.Info.IdeName} reports no problems{If(p.Length = 0, "", " in " & p)}.", "no problems")
            Dim sb As New StringBuilder()
            Dim errors = 0, warnings = 0
            For Each d In list.EnumerateArray()
                Dim sev = Jx.StrOf(d, "severity")
                If sev = "error" Then errors += 1
                If sev = "warning" Then warnings += 1
                Dim src = Jx.StrOf(d, "source")
                sb.Append(ctx.State.Display(Jx.StrOf(d, "path"))).Append(":"c).Append(Jx.IntOf(d, "line")).Append(":"c).Append(Jx.IntOf(d, "column")).Append(" ").
                   Append(sev).Append(": ").Append(Jx.StrOf(d, "message").Replace(vbLf, " ")).Append(If(src.Length > 0, " [" & src & "]", "")).Append(vbLf)
            Next
            Return ToolResult.Done(ToolKit.Clip(sb.ToString()), $"{errors} error{If(errors = 1, "", "s")}, {warnings} warning{If(warnings = 1, "", "s")}")
        End Function

    End Class

End Namespace
