Option Strict On
Option Explicit On
Option Infer On

Imports System.Text.Json
Imports System.Threading
Imports AgenticCli.Api
Imports AgenticCli.Core
Imports AgenticCli.Tools
Imports AgenticCli.Ui

Namespace Agent

    ''' <summary>A prompt saved in the web app (Chat → saved prompts); here it is a /command.</summary>
    Friend NotInheritable Class SavedPrompt
        Public Property Command As String
        Public Property Title As String
        Public Property Content As String
    End Class

    ' The server's chat features from the terminal: share links (/share), the memory the assistant keeps about you
    ' (/memories, /remember, /forget) and the account's saved prompts, which work as /commands.
    Partial Friend NotInheritable Class Session

        Private _savedPrompts As New List(Of SavedPrompt)

        Private Shared Function Esc(value As String) As String
            Return Uri.EscapeDataString(If(value, ""))
        End Function

        ''' <summary>Loads the account's saved prompts (an older server without them leaves the list empty).</summary>
        Private Async Function LoadSavedPromptsAsync() As Task
            Try
                Dim r = Await _api.GetAsync("/api/chat/prompts", CancellationToken.None).ConfigureAwait(False)
                If r.ValueKind <> JsonValueKind.Array Then Return
                _savedPrompts = r.EnumerateArray().Select(Function(p) New SavedPrompt With {
                    .Command = Jx.StrOf(p, "command"), .Title = Jx.StrOf(p, "title"), .Content = Jx.StrOf(p, "content")}).Where(Function(p) p.Command.Length > 0).ToList()
            Catch ex As Exception When TypeOf ex Is ApiException OrElse TypeOf ex Is Net.Http.HttpRequestException OrElse TypeOf ex Is OperationCanceledException
            End Try
        End Function

        Private Function FindSavedPrompt(name As String) As SavedPrompt
            Return _savedPrompts.FirstOrDefault(Function(p) p.Command.Equals(name, StringComparison.OrdinalIgnoreCase))
        End Function

        Private Async Function PromptsCommandAsync(rest As String) As Task
            Await LoadSavedPromptsAsync().ConfigureAwait(False)
            If _savedPrompts.Count = 0 Then
                Out.Note("No saved prompts. Save them in the web app (Chat → the prompt icon, or type / in the message box); they work here as /commands.")
                Return
            End If
            For Each p In _savedPrompts
                Out.Line("  " & Ansi.Accent(Ansi.Pad("/" & p.Command, 22)) & " " & Ansi.Muted(ToolKit.OneLine(If(String.IsNullOrWhiteSpace(p.Title), p.Content, p.Title), 80)))
            Next
            Out.Note("Type /name (and anything after it) to send a saved prompt.")
        End Function

        ' ------------------------------------------------------------------ share links

        Private Async Function ShareAsync(rest As String) As Task
            If String.IsNullOrEmpty(State.ChatId) Then
                Out.Note("Nothing to share yet: send a message first.")
                Return
            End If
            Dim path = $"/api/chat/conversations/{Esc(State.ChatId)}/share"
            If rest.Equals("off", StringComparison.OrdinalIgnoreCase) OrElse rest.Equals("stop", StringComparison.OrdinalIgnoreCase) Then
                Await _api.DeleteAsync(path, CancellationToken.None).ConfigureAwait(False)
                Out.Success("Sharing stopped: the link no longer works.")
                Return
            End If
            Dim r = Await _api.PostAsync(path, "{}", CancellationToken.None).ConfigureAwait(False)
            Dim url = _creds.ServerUrl.TrimEnd("/"c) & Jx.StrOf(r, "path", "/share/" & Jx.StrOf(r, "token"))
            ' Connected through 127.0.0.1 (a tunnel or the server's own machine): the server's public address is the one to share.
            Dim serverLink = Jx.StrOf(r, "url")
            Dim u As Uri = Nothing
            If Not String.IsNullOrEmpty(serverLink) AndAlso Uri.TryCreate(_creds.ServerUrl, UriKind.Absolute, u) AndAlso u.IsLoopback Then url = serverLink
            Out.Success("Anyone with this link can read the conversation (your reasoning, tool output and costs are left out):")
            Out.Line("  " & Ansi.Accent(url))
            If Clipboard.SetText(url) Then Out.Note("Copied to the clipboard. /share off stops sharing.")
        End Function

        ' ------------------------------------------------------------------ memory about you (kept by the server for all your chats)

        Private Async Function MemoriesCommandAsync(rest As String) As Task
            Select Case rest.ToLowerInvariant()
                Case "on", "off"
                    Await _api.SendAsync("PUT", "/api/chat/personalization", Jx.Stringify(New With {.memory = rest.ToLowerInvariant() = "on"}), CancellationToken.None).ConfigureAwait(False)
                    Out.Success("Memory " & rest.ToLowerInvariant() & If(rest.ToLowerInvariant() = "on", ": the assistant remembers what you share and uses it in later chats.", ": the assistant neither uses nor saves memories."))
                    Return
                Case "clear"
                    Dim n = Jx.IntOf(Await _api.PostAsync("/api/chat/memories/clear", "{}", CancellationToken.None).ConfigureAwait(False), "deleted")
                    Out.Success($"Forgot {n} memor{If(n = 1, "y", "ies")}.")
                    Return
            End Select
            Dim p = Await _api.GetAsync("/api/chat/personalization", CancellationToken.None).ConfigureAwait(False)
            Dim list = Jx.ArrOf(p, "memories")
            Out.Line($"  Memory is {If(Jx.BoolOf(p, "memory", True), Ansi.Accent("on"), Ansi.Muted("off"))} · {list.Count} memor{If(list.Count = 1, "y", "ies")} (shared with the web and desktop apps)")
            For Each m In list
                Out.Line("  " & Ansi.Muted(Ansi.Pad(Jx.StrOf(m, "id"), 30)) & " " & Jx.StrOf(m, "content"))
            Next
            Dim about = Jx.StrOf(p, "aboutMe")
            If about.Length > 0 Then Out.Line("  " & Ansi.Muted("About you: ") & ToolKit.OneLine(about, 100))
            Out.Note("/remember <fact> adds one · /forget <id or words> removes · /memories on|off|clear")
        End Function

        Private Async Function RememberOnServerAsync(fact As String) As Task
            If fact.Length = 0 Then
                Out.Fail("Usage: /remember <something the assistant should know about you in every chat>")
                Return
            End If
            Dim m = Await _api.PostAsync("/api/chat/memories", Jx.Stringify(New With {.content = fact}), CancellationToken.None).ConfigureAwait(False)
            Out.Success("Remembered: " & Jx.StrOf(m, "content"))
        End Function

        Private Async Function ForgetAsync(what As String) As Task
            If what.Length = 0 Then
                Out.Fail("Usage: /forget <memory id or words it contains>")
                Return
            End If
            Dim p = Await _api.GetAsync("/api/chat/personalization", CancellationToken.None).ConfigureAwait(False)
            Dim hits = Jx.ArrOf(p, "memories").Where(Function(m) Jx.StrOf(m, "id") = what OrElse
                                                                (what.Length >= 3 AndAlso Jx.StrOf(m, "content").IndexOf(what, StringComparison.OrdinalIgnoreCase) >= 0)).ToList()
            If hits.Count = 0 Then
                Out.Note("No memory matches. /memories lists them.")
                Return
            End If
            For Each m In hits
                Await _api.DeleteAsync("/api/chat/memories/" & Esc(Jx.StrOf(m, "id")), CancellationToken.None).ConfigureAwait(False)
                Out.Success("Forgot: " & Jx.StrOf(m, "content"))
            Next
        End Function

    End Class

End Namespace
