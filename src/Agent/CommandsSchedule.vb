Option Strict On
Option Explicit On
Option Infer On

Imports System.Text.Json
Imports System.Text.RegularExpressions
Imports System.Threading
Imports AgenticCli.Api
Imports AgenticCli.Core
Imports AgenticCli.Tools
Imports AgenticCli.Ui

Namespace Agent

    ' /schedule: prompts the server sends to this conversation on a schedule, also while the CLI is closed (the web app's /schedule).
    Partial Friend NotInheritable Class Session

        Private Shared ReadOnly ScheduleWhen As New Regex("^(every\s+\d+\s*(?:m|min|mins|minutes?|h|hr|hrs|hours?|d|days?)|hourly|daily\s+\d{1,2}:\d{2}|" &
                                                          "weekdays\s+\d{1,2}:\d{2}|at\s+\d{1,2}:\d{2}|once\s+\d{4}-\d{2}-\d{2}\s+\d{1,2}:\d{2})\s+([\s\S]+)$", RegexOptions.IgnoreCase)
        Private Const ScheduleHelp As String = "/schedule every 30m | hourly | daily 09:00 | weekdays 08:30 | at 14:00 | once 2026-11-01 09:00 <prompt>"

        ''' <summary>/schedule [list|all] | &lt;when&gt; &lt;prompt&gt; | pause|resume|delete|run &lt;# or id&gt;.</summary>
        Private Async Function ScheduleCommandAsync(arg As String) As Task
            Dim ct = CancellationToken.None
            Dim parts = arg.Split({" "c}, 2, StringSplitOptions.RemoveEmptyEntries)
            Dim verb = If(parts.Length > 0, parts(0).ToLowerInvariant(), "")
            Dim rest = If(parts.Length > 1, parts(1).Trim(), "")
            Try
                Select Case verb
                    Case "", "list", "all"
                        Await ShowSchedulesAsync(verb = "all" OrElse String.IsNullOrEmpty(State.ChatId), ct).ConfigureAwait(False)
                    Case "pause", "resume", "delete", "remove", "run"
                        Dim id = Await ScheduleIdAsync(rest, ct).ConfigureAwait(False)
                        If id Is Nothing Then
                            Out.Fail($"Which one? /schedule {verb} <# from /schedule list, or its id>")
                            Return
                        End If
                        Select Case verb
                            Case "pause", "resume"
                                Await _api.PatchAsync("/api/chat/schedules/" & Uri.EscapeDataString(id), Jx.Stringify(New With {.enabled = (verb = "resume")}), ct).ConfigureAwait(False)
                                Out.Success(If(verb = "pause", "Paused", "Resumed"))
                            Case "run"
                                Await _api.PostAsync("/api/chat/schedules/" & Uri.EscapeDataString(id) & "/run", "{}", ct).ConfigureAwait(False)
                                Out.Success("Started: the answer appears in its conversation")
                            Case Else
                                Await _api.DeleteAsync("/api/chat/schedules/" & Uri.EscapeDataString(id), ct).ConfigureAwait(False)
                                Out.Success("Deleted")
                        End Select
                    Case Else
                        Dim m = ScheduleWhen.Match(arg.Trim())
                        If Not m.Success Then
                            Out.Fail(ScheduleHelp)
                            Return
                        End If
                        If Not Await EnsureChatAsync().ConfigureAwait(False) Then Return
                        Dim created = Await _api.PostAsync("/api/chat/schedules", Jx.Stringify(New With {
                            .chatId = State.ChatId, .when = m.Groups(1).Value, .prompt = m.Groups(2).Value.Trim(), .timeZone = TimeZoneInfo.Local.Id}), ct).ConfigureAwait(False)
                        Out.Success($"Scheduled ({Jx.StrOf(created, "when")}): next {LocalTime(Jx.StrOf(created, "nextRunUtc"))}")
                        Out.Line(Ansi.Muted("  The server sends it to this conversation, also while the CLI is closed. /schedule lists, pauses and deletes them."))
                End Select
            Catch ex As ApiException
                Out.Fail(ex.Message)
            End Try
        End Function

        Private _scheduleIds As New List(Of String)

        Private Async Function ShowSchedulesAsync(all As Boolean, ct As CancellationToken) As Task
            Dim list = Await _api.GetAsync("/api/chat/schedules" & If(all, "", "?chatId=" & Uri.EscapeDataString(State.ChatId)), ct).ConfigureAwait(False)
            _scheduleIds = New List(Of String)
            If list.ValueKind <> JsonValueKind.Array OrElse list.GetArrayLength() = 0 Then
                Out.Note(If(all, "No scheduled prompts. ", "No scheduled prompts in this conversation. ") & ScheduleHelp)
                Return
            End If
            Dim i = 0
            For Each s In list.EnumerateArray()
                i += 1
                _scheduleIds.Add(Jx.StrOf(s, "id"))
                Dim enabled = Jx.Prop(s, "enabled").ValueKind = JsonValueKind.True
                Dim status = Jx.StrOf(s, "lastStatus", Nothing)
                Out.Line($"  #{i} " & Ansi.Bold(Jx.StrOf(s, "when")) & "  " & ToolKit.OneLine(Jx.StrOf(s, "prompt"), 70) &
                         Ansi.Muted("  " & If(all, ToolKit.OneLine(Jx.StrOf(s, "chatTitle", Jx.StrOf(s, "chatId")), 30) & " · ", "") &
                                    If(enabled, "next " & LocalTime(Jx.StrOf(s, "nextRunUtc")), "paused") &
                                    If(status Is Nothing, "", " · last: " & status)))
            Next
            Out.Line(Ansi.Muted("  /schedule pause|resume|delete|run <#>"))
        End Function

        ''' <summary>"#2" or "2" (from the last list) or the id itself.</summary>
        Private Async Function ScheduleIdAsync(text As String, ct As CancellationToken) As Task(Of String)
            Dim t = text.Trim().TrimStart("#"c)
            If t.Length = 0 Then Return Nothing
            Dim n As Integer
            If Integer.TryParse(t, n) Then
                If _scheduleIds.Count = 0 Then Await ShowSchedulesAsync(True, ct).ConfigureAwait(False)
                Return If(n >= 1 AndAlso n <= _scheduleIds.Count, _scheduleIds(n - 1), Nothing)
            End If
            Return t
        End Function

        Private Shared Function LocalTime(utcText As String) As String
            Dim d As DateTime
            If DateTime.TryParse(utcText, Globalization.CultureInfo.InvariantCulture, Globalization.DateTimeStyles.AdjustToUniversal Or Globalization.DateTimeStyles.AssumeUniversal, d) Then
                Return d.ToLocalTime().ToString("ddd yyyy-MM-dd HH:mm", Globalization.CultureInfo.InvariantCulture)
            End If
            Return utcText
        End Function

    End Class

End Namespace
