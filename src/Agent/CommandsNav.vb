Option Strict On
Option Explicit On
Option Infer On

Imports System.IO
Imports System.Net.Http
Imports System.Runtime.InteropServices
Imports System.Text
Imports System.Text.Json
Imports System.Threading
Imports AgenticCli.Api
Imports AgenticCli.Core
Imports AgenticCli.Tools
Imports AgenticCli.Ui

Namespace Agent

    ' What the chat tab of the other apps has that the CLI did not: /find in the conversation, /usage (the account's use),
    ' /feedback to the administrators, /pr-comments, /ide (VS Code: selection, problems, edits as diffs) and /plugin
    ' (Claude Code plugins: commands, agents, skills, hooks and MCP servers).
    Partial Friend NotInheritable Class Session

        ' ------------------------------------------------------------------ /find
        Private Async Function FindCommandAsync(rest As String) As Task
            Dim q = rest.Trim()
            If q.Length = 0 Then q = Menu.Ask(Ansi.Accent(Ansi.Pointer) & " Find in this conversation:").Trim()
            If q.Length = 0 Then Return
            If String.IsNullOrEmpty(State.ChatId) Then
                Out.Note("Nothing to search yet: this conversation has no messages.")
                Return
            End If
            Dim conv = Await _api.GetAsync("/api/chat/conversations/" & Uri.EscapeDataString(State.ChatId), CancellationToken.None).ConfigureAwait(False)
            Dim msgs = Jx.ArrOf(conv, "messages")
            Dim hits = 0, inMessages = 0
            Const MaxShown = 40
            For i = 0 To msgs.Count - 1
                Dim m = msgs(i)
                Dim role = Jx.StrOf(m, "role")
                Dim parts As New List(Of (Where As String, Text As String)) From {(If(role = "user", "You", "Answer"), Jx.StrOf(m, "content"))}
                Dim reasoning = Jx.StrOf(m, "reasoning")
                If reasoning.Length > 0 Then parts.Add(("thinking", reasoning))
                For Each t In Jx.ArrOf(m, "toolCalls")
                    parts.Add(("tool " & Jx.StrOf(t, "name"), Jx.StrOf(t, "arguments") & vbLf & Jx.StrOf(t, "result")))
                Next
                Dim found = False
                For Each p In parts
                    Dim at = p.Text.IndexOf(q, StringComparison.OrdinalIgnoreCase)
                    While at >= 0
                        hits += 1
                        found = True
                        If hits <= MaxShown Then
                            Dim from = Math.Max(0, at - 50)
                            Dim before = p.Text.Substring(from, at - from)
                            Dim match = p.Text.Substring(at, q.Length)
                            Dim afterLen = Math.Min(70, p.Text.Length - at - q.Length)
                            Dim after = p.Text.Substring(at + q.Length, afterLen)
                            Dim clean = Function(s As String) s.Replace(vbCr, " ").Replace(vbLf, " ").Replace(vbTab, " ")
                            Dim stamp = ""
                            Dim created As DateTime
                            If DateTime.TryParse(Jx.StrOf(m, "createdUtc"), Globalization.CultureInfo.InvariantCulture, Globalization.DateTimeStyles.AdjustToUniversal Or Globalization.DateTimeStyles.AssumeUniversal, created) Then stamp = " · " & created.ToLocalTime().ToString("MMM d HH:mm")
                            Out.Line($"  {Ansi.Muted("#" & (i + 1).ToString())} {Ansi.Bold(p.Where)}{Ansi.Muted(stamp)}  {If(from > 0, "…", "")}{clean(before)}{Ansi.Accent(Ansi.Bold(clean(match)))}{clean(after)}{If(at + q.Length + afterLen < p.Text.Length, "…", "")}")
                        End If
                        at = p.Text.IndexOf(q, at + Math.Max(1, q.Length), StringComparison.OrdinalIgnoreCase)
                    End While
                Next
                If found Then inMessages += 1
            Next
            If hits = 0 Then
                Out.Note($"No match for ""{q}"" in the {msgs.Count} messages of this conversation.")
            Else
                Out.Note($"{hits} match{If(hits = 1, "", "es")} in {inMessages} message{If(inMessages = 1, "", "s")}{If(hits > MaxShown, $" (the first {MaxShown} shown)", "")} · #n is the message's number · /rewind goes back to one")
            End If
        End Function

        ' ------------------------------------------------------------------ /usage
        Private Shared Function Money(micros As Long) As String
            Dim v = micros / 1000000D
            Return "$" & If(v = 0, "0", If(v < 1, v.ToString("0.####", Globalization.CultureInfo.InvariantCulture), v.ToString("0.00", Globalization.CultureInfo.InvariantCulture)))
        End Function

        Private Async Function UsageCommandAsync() As Task
            Dim u = Await _api.GetAsync("/api/chat/usage", CancellationToken.None).ConfigureAwait(False)
            Dim hasAccount = Jx.BoolOf(u, "hasAccount")
            Dim line = Sub(label As String, key As String)
                           Dim o As JsonElement
                           If Not u.TryGetProperty(key, o) Then Return
                           Dim inT = Jx.LongOf(o, "input"), outT = Jx.LongOf(o, "output")
                           Out.Line($"  {Ansi.Pad(label, 14)}{Ansi.Bold(TurnRunner.FormatTokens(inT + outT) & " tokens")}  {Ansi.Muted($"{TurnRunner.FormatTokens(inT)} in · {TurnRunner.FormatTokens(outT)} out · {Jx.LongOf(o, "calls")} calls{If(hasAccount, " · " & Money(Jx.LongOf(o, "costMicros")), "")}")}")
                       End Sub
            Out.Line(Ansi.Bold("Your usage") & Ansi.Muted($" · {Jx.StrOf(u, "username")} · every app and the API"))
            line("Today", "today")
            line("This month", "month")
            line("Last 30 days", "days30")
            If hasAccount Then Out.Line($"  {Ansi.Pad("Balance", 14)}{Ansi.Bold(Money(Jx.LongOf(u, "balanceMicros")))}")
            Dim limits As JsonElement
            If u.TryGetProperty("limits", limits) Then
                Dim daily = Jx.LongOf(limits, "dailyTokens"), used = Jx.LongOf(limits, "usedToday")
                If daily > 0 Then
                    Dim pct = CInt(Math.Min(100, used * 100 \ Math.Max(1, daily)))
                    Dim filled = pct \ 5
                    Dim resets As DateTime
                    Dim left = If(DateTime.TryParse(Jx.StrOf(limits, "resetsUtc"), Globalization.CultureInfo.InvariantCulture, Globalization.DateTimeStyles.AdjustToUniversal Or Globalization.DateTimeStyles.AssumeUniversal, resets),
                                  resets - DateTime.UtcNow, TimeSpan.Zero)
                    Dim bar = New String("█"c, filled) & New String("░"c, 20 - filled)
                    Out.Line($"  {Ansi.Pad("Daily limit", 14)}{If(pct >= 90, Ansi.Bad(bar), Ansi.Accent(bar))} {TurnRunner.FormatTokens(used)} of {TurnRunner.FormatTokens(daily)} · resets in {CInt(Math.Max(0, left.TotalHours))}h {Math.Max(0, left.Minutes)}m")
                End If
                Dim rpm = Jx.IntOf(limits, "requestsPerMinute")
                If rpm > 0 Then Out.Line($"  {Ansi.Pad("Rate limit", 14)}{rpm} requests a minute")
            End If
            Dim days = Jx.ArrOf(u, "daily")
            If days.Count > 0 Then
                Dim ticks = "▁▂▃▄▅▆▇█"
                Dim max = Math.Max(1L, days.Max(Function(d) Jx.LongOf(d, "input") + Jx.LongOf(d, "output")))
                Dim spark = String.Concat(days.Select(Function(d) ticks(CInt(Math.Min(7, (Jx.LongOf(d, "input") + Jx.LongOf(d, "output")) * 7 \ max)))))
                Out.Line($"  {Ansi.Pad("30 days", 14)}{Ansi.Accent(spark)}")
            End If
            Dim models = Jx.ArrOf(u, "byModel")
            If models.Count > 0 Then
                Out.Line(Ansi.Muted("  By model, last 30 days:"))
                For Each m In models.Take(12)
                    Dim total = Jx.LongOf(m, "input") + Jx.LongOf(m, "output")
                    Out.Line($"    {Ansi.Pad(ToolKit.OneLine(Jx.StrOf(m, "model"), 34), 36)}{Ansi.Pad(TurnRunner.FormatTokens(total), 9)}{Ansi.Muted($"{Jx.LongOf(m, "calls")} calls{If(hasAccount, " · " & Money(Jx.LongOf(m, "costMicros")), "")}{If(Jx.LongOf(m, "todayTokens") > 0, " · today " & TurnRunner.FormatTokens(Jx.LongOf(m, "todayTokens")), "")}")}")
                Next
            End If
            Out.Note("  /cost shows this session only.")
        End Function

        ' ------------------------------------------------------------------ /feedback
        Private Async Function FeedbackCommandAsync(rest As String) As Task
            Dim text = rest.Trim()
            If text.Length = 0 Then text = Menu.Ask(Ansi.Accent(Ansi.Pointer) & " What happened? What did you expect? (Enter to cancel)").Trim()
            If text.Length = 0 Then
                Await ShowFeedbackRepliesAsync(True).ConfigureAwait(False)
                Return
            End If
            Dim includeChat = Not String.IsNullOrEmpty(State.ChatId) AndAlso Menu.Confirm("Include this conversation (its transcript goes with the report)?", True)
            Dim logs As New StringBuilder()
            logs.AppendLine($"Agentic CLI {AppInfo.Version} · {RuntimeInformation.OSDescription} · {RuntimeInformation.ProcessArchitecture} · .NET {Environment.Version}")
            logs.AppendLine($"Folder: {State.ProjectDir} · model {_model} · mode {State.ModeLabel}")
            logs.AppendLine($"Server: {_api.BaseUrl}")
            logs.AppendLine($"MCP: {String.Join(", ", State.Mcp.ConnectedServers().Select(Function(c) c.Options.Id))}")
            logs.AppendLine($"Editor: {If(State.Ide Is Nothing, "none", State.Ide.Info.Label)}")
            Out.StartSpinner("Sending…")
            Dim r As JsonElement
            Try
                r = Await _api.PostAsync("/api/chat/feedback", Jx.Stringify(New With {
                    .text = text, .chatId = If(includeChat, State.ChatId, Nothing), .includeChat = includeChat, .logs = logs.ToString(),
                    .app = "CLI", .version = AppInfo.Version, .platform = RuntimeInformation.OSDescription}), CancellationToken.None).ConfigureAwait(False)
            Finally
                Out.StopSpinner()
            End Try
            Out.Success("Sent to the administrators' Inbox" & If(Jx.StrOf(r, "fileName").Length > 0, " (" & Jx.StrOf(r, "fileName") & ")", "") & ". Their reply shows with /feedback.")
        End Function

        Private Async Function ShowFeedbackRepliesAsync(onlyReplies As Boolean) As Task
            Dim list As List(Of JsonElement)
            Try
                list = Jx.ArrOf(Jx.Parse("{""a"":" & (Await _api.SendAsync("GET", "/api/chat/feedback", Nothing, CancellationToken.None).ConfigureAwait(False)).GetRawText() & "}"), "a")
            Catch ex As ApiException
                Return
            End Try
            Dim shown = list.Where(Function(f) Not onlyReplies OrElse Jx.StrOf(f, "reply").Length > 0).Take(8).ToList()
            If shown.Count = 0 Then Return
            Out.Line(Ansi.Muted("  Your earlier reports:"))
            For Each f In shown
                Out.Line($"  {Ansi.Muted("·")} {ToolKit.OneLine(Jx.StrOf(f, "text"), 80)} {Ansi.Muted("(" & If(Jx.StrOf(f, "status") = "Pending", "waiting", Jx.StrOf(f, "status").ToLowerInvariant()) & ")")}")
                If Jx.StrOf(f, "reply").Length > 0 Then Out.Line($"      {Ansi.Accent(If(Jx.StrOf(f, "by").Length > 0, Jx.StrOf(f, "by"), "admin") & ":")} {Jx.StrOf(f, "reply")}")
            Next
        End Function

        ' ------------------------------------------------------------------ /pr-comments
        Private Shared Function PrCommentsPrompt(pr As String) As String
            Dim which = If(pr.Length > 0, "pull request " & pr, "the pull request of the current branch")
            Return $"Work through the review comments on {which}." & vbLf &
                $"1. Find it: `gh pr view {pr} --json number,url,title,headRefName,baseRefName` (GitHub). For Gitea or another host, use its CLI (tea) or its API with the git remote's address." & vbLf &
                $"2. Read every comment, the inline ones with their file and line too: `gh api repos/{{owner}}/{{repo}}/pulls/{{number}}/comments`, `gh api repos/{{owner}}/{{repo}}/pulls/{{number}}/reviews` and `gh pr view {pr} --comments`." & vbLf &
                "3. List them briefly, then fix each one in the code (or say why it should not change). Check your fixes (build, tests)." & vbLf &
                "4. End with a table: comment (file:line) → what you changed. Do not push or reply on the pull request unless I ask."
        End Function

        ' ------------------------------------------------------------------ /ide
        Private Async Function IdeCommandAsync(rest As String) As Task
            Dim arg = rest.Trim().ToLowerInvariant()
            If arg = "off" OrElse arg = "disconnect" Then
                If State.Ide Is Nothing Then Out.Note("No editor is connected.") Else Out.Success("Disconnected from " & State.Ide.Info.IdeName & ".")
                State.Ide = Nothing
                Return
            End If
            Dim found = IdeLink.Available(State.ProjectDir)
            If arg = "" AndAlso State.Ide IsNot Nothing Then
                Dim ide = State.Ide
                Out.Line($"  Connected to {Ansi.Bold(ide.Info.IdeName)} {Ansi.Muted(If(ide.Info.Version, ""))} · {ide.Info.MatchedFolder}")
                Try
                    Dim s = Await ide.StateAsync(CancellationToken.None).ConfigureAwait(False)
                    Dim active = Jx.StrOf(s, "activeFile")
                    Dim sel As JsonElement
                    Out.Line("  Open: " & If(active.Length > 0, State.Display(active), Ansi.Muted("no file")) &
                             If(s.TryGetProperty("selection", sel) AndAlso sel.ValueKind = JsonValueKind.Object, Ansi.Muted($" · lines {Jx.IntOf(sel, "startLine")}-{Jx.IntOf(sel, "endLine")} selected"), ""))
                Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is HttpRequestException OrElse TypeOf ex Is OperationCanceledException
                    Out.Warning($"{ide.Info.IdeName} does not answer ({ex.Message}). /ide connects again, /ide off disconnects.")
                End Try
                Out.Note("  Your selection (or the open file) goes with each message, edits show as diffs there, and the IdeDiagnostics tool reads its problems. /ide off disconnects.")
                Return
            End If
            If found.Count = 0 Then
                Out.Note("No editor found. Open this folder in VS Code with Agentic for VS Code (0.2.24 or newer; the setting agentic.ide.bridge is on), then /ide again.")
                Return
            End If
            Dim pick As IdeInfo
            Dim n As Integer
            If Integer.TryParse(arg, n) AndAlso n >= 1 AndAlso n <= found.Count Then
                pick = found(n - 1)
            ElseIf arg = "list" OrElse found.Count > 1 Then
                Dim i = Menu.Select(" Connect to which editor?", found.Select(Function(f) New MenuOption(f.Label & If(f.MatchedFolder Is Nothing, Ansi.Muted(" (another folder)"), ""))).ToList())
                If i < 0 Then Return
                pick = found(i)
            Else
                pick = found(0)
            End If
            Dim link As New IdeLink(pick)
            Try
                Await link.StateAsync(CancellationToken.None).ConfigureAwait(False)
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is HttpRequestException OrElse TypeOf ex Is OperationCanceledException
                Out.Fail($"{pick.IdeName} did not answer: {ex.Message}")
                Return
            End Try
            State.Ide = link
            Out.Success($"Connected to {pick.Label}. Your selection (or the open file) goes with each message, edits show as diffs in {pick.IdeName}, and IdeDiagnostics reads its problems.")
            If pick.MatchedFolder Is Nothing Then Out.Warning("Its workspace does not hold this folder: paths may not line up.")
        End Function

        ' ------------------------------------------------------------------ /plugin
        Private Function PluginCountsText(c As PluginCounts) As String
            Dim parts As New List(Of String)
            If c.Commands > 0 Then parts.Add($"{c.Commands} command{If(c.Commands = 1, "", "s")}")
            If c.Agents > 0 Then parts.Add($"{c.Agents} agent{If(c.Agents = 1, "", "s")}")
            If c.Skills > 0 Then parts.Add($"{c.Skills} skill{If(c.Skills = 1, "", "s")}")
            If c.Hooks > 0 Then parts.Add($"{c.Hooks} hook{If(c.Hooks = 1, "", "s")}")
            If c.Mcp > 0 Then parts.Add($"{c.Mcp} MCP server{If(c.Mcp = 1, "", "s")}")
            Return If(parts.Count = 0, "nothing to load", String.Join(" · ", parts))
        End Function

        Private Async Function PluginCommandAsync(rest As String) As Task
            Dim words = rest.Trim().Split({" "c}, StringSplitOptions.RemoveEmptyEntries)
            Dim verb = If(words.Length > 0, words(0).ToLowerInvariant(), "list")
            Dim arg = If(words.Length > 1, String.Join(" ", words.Skip(1)), "")
            Dim dirs = {State.ProjectDir}
            Dim changed = False
            Select Case verb
                Case "list", "ls"
                    Dim all = Plugins.Installed(dirs)
                    If all.Count = 0 Then
                        Out.Note("No plugins. Add a marketplace (/plugin marketplace add owner/repo), then /plugin browse and /plugin install <name>.")
                    End If
                    For Each p In all
                        Dim hooksNote = If(p.Counts.Hooks > 0 AndAlso Not p.HooksOn AndAlso p.Enabled, Ansi.Muted(" (hooks run once you turn it on here: /plugin enable " & p.Id & ")"), "")
                        Out.Line($"  {If(p.Enabled, Ansi.Ok(Ansi.Dot), Ansi.Muted(Ansi.Dot))} {Ansi.Bold(p.Id)} {Ansi.Muted(If(p.Version.Length > 0, p.Version & " · ", "") & p.Source)}{If(p.Enabled, "", Ansi.Muted(" (off)"))}")
                        If p.Description.Length > 0 Then Out.Line("      " & ToolKit.OneLine(p.Description, 100))
                        Out.Line("      " & Ansi.Muted(PluginCountsText(p.Counts)) & hooksNote)
                    Next
                    Dim markets = Plugins.Marketplaces()
                    If markets.Count > 0 Then Out.Note($"  Marketplaces: {String.Join(", ", markets.Select(Function(m) $"{m.Name} ({m.Plugins.Count})"))} · /plugin browse lists their plugins")
                    Out.Note("  /plugin enable|disable <name> · install <name>[@marketplace] · uninstall <name> · marketplace add|remove|update|list")
                    Return
                Case "enable", "disable", "on", "off"
                    Dim p = Plugins.Find(arg, dirs)
                    If p Is Nothing Then
                        Out.Fail("No installed plugin " & arg & ". /plugin lists them.")
                        Return
                    End If
                    Dim isOn = verb = "enable" OrElse verb = "on"
                    Plugins.SetEnabled(p.Id, isOn)
                    Out.Success($"{p.Id} is {If(isOn, "on", "off")}{If(isOn AndAlso p.Counts.Hooks > 0, " (its hooks run too)", "")}. Claude Code's own settings are not changed.")
                    changed = True
                Case "install", "add"
                    If arg.Length = 0 Then
                        Out.Fail("Usage: /plugin install <name>[@marketplace]")
                        Return
                    End If
                    Out.StartSpinner("Installing " & arg & "…")
                    Dim id As String
                    Try
                        id = Await Plugins.InstallAsync(arg).ConfigureAwait(False)
                    Finally
                        Out.StopSpinner()
                    End Try
                    Dim p = Plugins.Find(id, dirs)
                    Out.Success($"Installed {id}: {If(p Is Nothing, "", PluginCountsText(p.Counts))}.")
                    changed = True
                Case "uninstall", "remove", "rm"
                    Dim p = Plugins.Find(arg, dirs)
                    If p Is Nothing Then
                        Out.Fail("No installed plugin " & arg & ".")
                        Return
                    End If
                    Plugins.Uninstall(p.Id)
                    Out.Success("Uninstalled " & p.Id & ".")
                    changed = True
                Case "browse", "available"
                    Dim installed = Plugins.Installed(dirs).Select(Function(x) x.Id).ToHashSet(StringComparer.OrdinalIgnoreCase)
                    Dim markets = Plugins.Marketplaces().Where(Function(m) arg.Length = 0 OrElse m.Name.Equals(arg, StringComparison.OrdinalIgnoreCase)).ToList()
                    If markets.Count = 0 Then
                        Out.Note("No marketplace. Add one: /plugin marketplace add owner/repo (or a git URL or a folder).")
                        Return
                    End If
                    For Each m In markets
                        Out.Line($"  {Ansi.Bold(m.Name)} {Ansi.Muted(m.Source)}")
                        For Each p In m.Plugins
                            Out.Line($"    {If(installed.Contains(p.Name & "@" & m.Name), Ansi.Ok("✓"), " ")} {Ansi.Pad(p.Name, 28)}{Ansi.Muted(ToolKit.OneLine(p.Description, 80))}")
                        Next
                    Next
                    Out.Note("  /plugin install <name>@<marketplace>")
                    Return
                Case "marketplace", "market"
                    Dim sub2 = If(words.Length > 1, words(1).ToLowerInvariant(), "list")
                    Dim value = If(words.Length > 2, String.Join(" ", words.Skip(2)), "")
                    Select Case sub2
                        Case "add"
                            Out.StartSpinner("Adding the marketplace…")
                            Dim name As String
                            Try
                                name = Await Plugins.AddMarketplaceAsync(value).ConfigureAwait(False)
                            Finally
                                Out.StopSpinner()
                            End Try
                            Dim m = Plugins.Marketplaces().FirstOrDefault(Function(x) x.Name = name)
                            Out.Success($"Added the marketplace {name}: {If(m?.Plugins.Count, 0)} plugin(s). /plugin browse {name} lists them.")
                        Case "remove", "rm"
                            Plugins.RemoveMarketplace(value)
                            Out.Success("Removed the marketplace " & value & ".")
                        Case "update"
                            Out.StartSpinner("Updating…")
                            Try
                                Await Plugins.UpdateMarketplaceAsync(value).ConfigureAwait(False)
                            Finally
                                Out.StopSpinner()
                            End Try
                            Out.Success("Updated " & value & ".")
                        Case Else
                            For Each m In Plugins.Marketplaces()
                                Out.Line($"  {Ansi.Bold(m.Name)} {Ansi.Muted($"{m.Plugins.Count} plugins · {m.Source} · {If(m.Removable, "Agentic", "Claude Code")}")}")
                            Next
                    End Select
                    Return
                Case Else
                    Out.Fail("Usage: /plugin [list | enable <name> | disable <name> | install <name> | uninstall <name> | browse | marketplace add|remove|update|list]")
                    Return
            End Select
            If changed Then
                ' What the plugins bring is loaded again: commands and skills, agents (read per message), hooks (in a few seconds) and MCP servers.
                ReloadSkills()
                Try
                    Await ConnectMcpAsync().ConfigureAwait(False)
                Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is InvalidOperationException
                    Out.Warning("MCP servers: " & ex.Message)
                End Try
            End If
        End Function

    End Class

End Namespace
