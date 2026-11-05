Option Strict On
Option Explicit On
Option Infer On

Imports System.IO
Imports System.Text
Imports System.Text.Json
Imports System.Text.Json.Nodes

Namespace Core

    ''' <summary>
    ''' Token usage in Claude Code's log format, so `npx ccusage` counts Agentic answers too: one JSON line per answer in
    ''' &lt;claude dir&gt;/projects/agentic-cli-&lt;project&gt;/&lt;chat id&gt;.jsonl. The claude dir is the first of CLAUDE_CONFIG_DIR when set;
    ''' otherwise ~/.config/claude when ~/.claude exists (ccusage reads both, and Claude Code's own /resume list stays clean),
    ''' else ~/.claude. AGENTIC_CCUSAGE_DIR overrides it; the setting "ccusageLog": false switches it off.
    ''' </summary>
    Friend NotInheritable Class UsageLog

        Private Sub New()
        End Sub

        Private Shared ReadOnly WriteLock As New Object()

        ''' <summary>x.y.z only: ccusage skips lines whose version is not a plain version number.</summary>
        Public Shared Function PlainVersion(v As String) As String
            Dim m = Text.RegularExpressions.Regex.Match(If(v, ""), "\d+\.\d+\.\d+")
            Return If(m.Success, m.Value, "0.0.0")
        End Function

        Public Shared Function ClaudeDataDir() As String
            Dim custom = Environment.GetEnvironmentVariable("AGENTIC_CCUSAGE_DIR")
            If Not String.IsNullOrWhiteSpace(custom) Then Return custom.Trim()
            Dim fromEnv = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR")
            If Not String.IsNullOrWhiteSpace(fromEnv) Then
                Dim first = fromEnv.Split(","c).Select(Function(p) p.Trim()).FirstOrDefault(Function(p) p.Length > 0)
                If first IsNot Nothing Then Return first
            End If
            Dim home = Paths.Home()
            Return If(Directory.Exists(Path.Combine(home, ".claude")), Path.Combine(home, ".config", "claude"), Path.Combine(home, ".claude"))
        End Function

        ''' <summary>Appends one answer's usage. <paramref name="message"/> is the answer from the server's "done" event.</summary>
        Public Shared Sub Record(chatId As String, cwd As String, message As JsonElement, cachedTokens As Long)
            Try
                If message.ValueKind <> JsonValueKind.Object OrElse String.IsNullOrWhiteSpace(chatId) Then Return
                Dim prompt = Jx.LongOf(message, "promptTokens"), completion = Jx.LongOf(message, "completionTokens")
                If prompt <= 0 AndAlso completion <= 0 Then Return
                Dim id = Jx.StrOf(message, "id", Guid.NewGuid().ToString("N"))
                Dim model = Jx.StrOf(message, "routedModel", Nothing)
                If String.IsNullOrWhiteSpace(model) Then model = Jx.StrOf(message, "model", "agentic")
                Dim cached = Math.Clamp(cachedTokens, 0L, prompt)
                Dim entry As New JsonObject From {
                    {"type", "assistant"}, {"timestamp", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")}, {"sessionId", chatId},
                    {"cwd", cwd}, {"version", PlainVersion(AppInfo.Version)}, {"requestId", id}, {"uuid", id}, {"isSidechain", False},
                    {"message", New JsonObject From {
                        {"id", id}, {"type", "message"}, {"role", "assistant"}, {"model", model},
                        {"usage", New JsonObject From {{"input_tokens", prompt - cached}, {"output_tokens", completion},
                                                       {"cache_creation_input_tokens", 0}, {"cache_read_input_tokens", cached}}}}}}
                Dim cost As JsonElement
                If message.TryGetProperty("costMicros", cost) AndAlso cost.ValueKind = JsonValueKind.Number Then entry("costUSD") = cost.GetInt64() / 1000000.0
                Dim dir = Path.Combine(ClaudeDataDir(), "projects", "agentic-cli-" & Paths.ProjectKey(If(String.IsNullOrWhiteSpace(cwd), Paths.Home(), cwd)))
                Dim safeId = String.Concat(chatId.Where(Function(c) Char.IsLetterOrDigit(c) OrElse c = "-"c OrElse c = "_"c))
                SyncLock WriteLock
                    Directory.CreateDirectory(dir)
                    File.AppendAllText(Path.Combine(dir, safeId & ".jsonl"), entry.ToJsonString() & vbLf, New UTF8Encoding(False))
                End SyncLock
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException OrElse TypeOf ex Is InvalidOperationException
                ' Usage logging never gets in the way of the answer.
            End Try
        End Sub

    End Class

End Namespace
