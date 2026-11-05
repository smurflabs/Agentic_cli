Option Strict On
Option Explicit On
Option Infer On

Imports System.IO
Imports System.Text
Imports System.Text.Json
Imports System.Text.RegularExpressions
Imports AgenticCli.Core

Namespace Agent

    Friend NotInheritable Class HookOutcome
        ''' <summary>Exit code 2, or {"decision":"block"} / permissionDecision "deny": the action does not happen.</summary>
        Public Property Blocked As Boolean
        Public Property Reason As String
        ''' <summary>Text added to the model's context (UserPromptSubmit / SessionStart stdout, additionalContext).</summary>
        Public Property Context As String
        ''' <summary>PreToolUse: allow | deny | ask from a hook's JSON answer.</summary>
        Public Property PermissionDecision As String
        ''' <summary>{"continue": false}: stop the session's work.</summary>
        Public Property StopAll As Boolean
        Public Property SystemMessage As String
        Public Property Ran As Integer
        ''' <summary>PreToolUse / PermissionRequest hookSpecificOutput.updatedInput: the tool runs with these arguments instead.</summary>
        Public Property UpdatedInput As JsonElement?
    End Class

    ''' <summary>
    ''' Hooks configured in settings, in Claude Code's format: commands for PreToolUse, PermissionRequest, PostToolUse,
    ''' PostToolUseFailure, UserPromptSubmit, Stop, StopFailure, SubagentStart, SubagentStop, SessionStart, SessionEnd,
    ''' Notification, PreCompact, PostCompact and CwdChanged. A hook gets the event as JSON on stdin; exit code 2
    ''' blocks (its stderr goes to the model or the user), exit 0 with JSON output can decide, other codes are reported.
    ''' </summary>
    Friend NotInheritable Class HookRunner

        Private ReadOnly _state As AgentState
        Public Property Enabled As Boolean = True

        Public Sub New(state As AgentState)
            _state = state
        End Sub

        Public Function Count(eventName As String) As Integer
            Return _state.Settings.Hooks(eventName).Count
        End Function

        ''' <summary>Runs every hook for the event whose matcher fits target (a tool name; "" for events without one).</summary>
        Public Function Run(eventName As String, target As String, payload As Dictionary(Of String, Object)) As HookOutcome
            Dim result As New HookOutcome()
            If Not Enabled Then Return result
            Dim hooks = _state.Settings.Hooks(eventName).Where(Function(h) MatcherFits(h.Matcher, target)).ToList()
            If hooks.Count = 0 Then Return result
            payload("hook_event_name") = eventName
            payload("session_id") = If(_state.ChatId, "")
            payload("cwd") = If(_state.Cwd, _state.ProjectDir)
            payload("permission_mode") = _state.ModeLabel
            Dim json = Jx.Stringify(payload)
            Dim context As New StringBuilder()
            For Each h In hooks
                result.Ran += 1
                Dim r = Execute(h, json)
                If r.ExitCode = 2 Then
                    result.Blocked = True
                    result.Reason = If(String.IsNullOrWhiteSpace(r.Stderr), "Blocked by a hook.", r.Stderr.Trim())
                    Exit For
                End If
                If r.ExitCode <> 0 Then
                    Ui.Out.Line(Ui.Ansi.Warn($"  {eventName} hook failed (exit {r.ExitCode}): ") & Ui.Ansi.Muted(ToolsShort(If(r.Stderr, r.Stdout))))
                    Continue For
                End If
                Dim out = If(r.Stdout, "").Trim()
                Dim answer As JsonElement
                If out.StartsWith("{") AndAlso Jx.TryParse(out, answer) Then
                    If Jx.Has(answer, "continue") AndAlso Not Jx.BoolOf(answer, "continue", True) Then
                        result.StopAll = True
                        result.Reason = Jx.StrOf(answer, "stopReason", "Stopped by a hook.")
                    End If
                    Dim decision = Jx.StrOf(answer, "decision", "").ToLowerInvariant()
                    If decision = "block" Then
                        result.Blocked = True
                        result.Reason = Jx.StrOf(answer, "reason", "Blocked by a hook.")
                    ElseIf decision = "approve" Then
                        result.PermissionDecision = "allow"
                    End If
                    Dim specific = Jx.Prop(answer, "hookSpecificOutput")
                    If specific.ValueKind = JsonValueKind.Object Then
                        Dim pd = Jx.StrOf(specific, "permissionDecision", Nothing)
                        If pd IsNot Nothing Then
                            result.PermissionDecision = pd.ToLowerInvariant()
                            If result.PermissionDecision = "deny" Then
                                result.Blocked = True
                                result.Reason = Jx.StrOf(specific, "permissionDecisionReason", "Denied by a hook.")
                            End If
                        End If
                        ' PermissionRequest: {"decision": {"behavior": "allow"|"deny", "message": …, "updatedInput": {…}}}.
                        Dim dec = Jx.Prop(specific, "decision")
                        If dec.ValueKind = JsonValueKind.Object Then
                            Dim behavior = Jx.StrOf(dec, "behavior", "").ToLowerInvariant()
                            If behavior = "allow" OrElse behavior = "deny" Then result.PermissionDecision = behavior
                            If behavior = "deny" Then result.Reason = Jx.StrOf(dec, "message", "Denied by a hook.")
                            Dim decInput = Jx.Prop(dec, "updatedInput")
                            If decInput.ValueKind = JsonValueKind.Object Then result.UpdatedInput = decInput.Clone()
                        End If
                        Dim updated = Jx.Prop(specific, "updatedInput")
                        If updated.ValueKind = JsonValueKind.Object Then result.UpdatedInput = updated.Clone()
                        Dim extra = Jx.StrOf(specific, "additionalContext", Nothing)
                        If extra IsNot Nothing Then context.AppendLine(extra)
                    End If
                    Dim sys = Jx.StrOf(answer, "systemMessage", Nothing)
                    If sys IsNot Nothing Then result.SystemMessage = sys
                ElseIf out.Length > 0 AndAlso (eventName = "UserPromptSubmit" OrElse eventName = "SessionStart") Then
                    context.AppendLine(out)
                End If
                If result.Blocked OrElse result.StopAll Then Exit For
            Next
            If context.Length > 0 Then result.Context = context.ToString().Trim()
            Return result
        End Function

        Private Shared Function ToolsShort(s As String) As String
            Dim one = If(s, "").Replace(ChrW(13), "").Replace(ChrW(10), " ").Trim()
            Return If(one.Length > 200, one.Substring(0, 200) & "…", one)
        End Function

        ''' <summary>Claude Code matchers: empty or * for all, a tool name, or a regular expression ("Edit|Write", "mcp__.*").</summary>
        Public Shared Function MatcherFits(matcher As String, target As String) As Boolean
            If String.IsNullOrWhiteSpace(matcher) OrElse matcher = "*" Then Return True
            If String.IsNullOrEmpty(target) Then Return True
            If matcher.Equals(target, StringComparison.OrdinalIgnoreCase) Then Return True
            Try
                Return Regex.IsMatch(target, "^(?:" & matcher & ")$", RegexOptions.IgnoreCase)
            Catch ex As ArgumentException
                Return False
            End Try
        End Function

        Private Function Execute(h As HookEntry, json As String) As (ExitCode As Integer, Stdout As String, Stderr As String)
            Try
                Dim psi As ProcessStartInfo
                If OperatingSystem.IsWindows() Then
                    ' cmd /s /c "…" runs the text between the outer quotes as it is. (ArgumentList would escape quotes inside the
                    ' command as \", which cmd does not understand: a quoted path or JSON in a hook command broke.)
                    psi = New ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/d /s /c """ & h.Command & """")
                Else
                    psi = New ProcessStartInfo("/bin/sh")
                    psi.ArgumentList.Add("-c")
                    psi.ArgumentList.Add(h.Command)
                End If
                psi.UseShellExecute = False
                psi.CreateNoWindow = True
                psi.RedirectStandardInput = True
                psi.RedirectStandardOutput = True
                psi.RedirectStandardError = True
                psi.StandardOutputEncoding = New UTF8Encoding(False)
                psi.StandardErrorEncoding = New UTF8Encoding(False)
                psi.WorkingDirectory = _state.ProjectDir
                For Each kv In _state.Settings.EnvVars()
                    psi.Environment(kv.Key) = kv.Value
                Next
                psi.Environment("AGENTIC_PROJECT_DIR") = _state.ProjectDir
                psi.Environment("CLAUDE_PROJECT_DIR") = _state.ProjectDir
                Using p = Process.Start(psi)
                    p.StandardInput.Write(json)
                    p.StandardInput.Close()
                    Dim o = p.StandardOutput.ReadToEndAsync()
                    Dim e = p.StandardError.ReadToEndAsync()
                    If Not p.WaitForExit(h.TimeoutSeconds * 1000) Then
                        Try
                            p.Kill(True)
                        Catch ex As InvalidOperationException
                        End Try
                        Return (1, "", $"timed out after {h.TimeoutSeconds} s")
                    End If
                    Return (p.ExitCode, o.GetAwaiter().GetResult(), e.GetAwaiter().GetResult())
                End Using
            Catch ex As Exception When TypeOf ex Is ComponentModel.Win32Exception OrElse TypeOf ex Is IOException OrElse TypeOf ex Is InvalidOperationException
                Return (1, "", ex.Message)
            End Try
        End Function

    End Class

End Namespace
