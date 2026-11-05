Option Strict On
Option Explicit On
Option Infer On

Imports System.IO
Imports System.Text
Imports System.Text.Json.Nodes
Imports System.Threading
Imports AgenticCli.Api
Imports AgenticCli.Core
Imports AgenticCli.Tools
Imports AgenticCli.Ui

Namespace Agent

    ' Slash commands that mirror Claude Code's: /btw, /recap, /branch, /diff, /cd, /release-notes, /statusline, /reload-skills.
    Partial Friend NotInheritable Class Session

        ''' <summary>The server model for side questions: the session's, or the server's default while the local Claude Code CLI answers.</summary>
        Private Function SideModel() As String
            Return If(IsClaudeModel(_model), _meta.DefaultModel, _model)
        End Function

        ''' <summary>The conversation so far as plain text for a side question (/btw, /recap): its summary, then the newest messages.</summary>
        Private Async Function ConversationDigestAsync(maxMessages As Integer) As Task(Of String)
            If String.IsNullOrEmpty(State.ChatId) Then Return ""
            Dim c = Await _chat.GetAsync(State.ChatId, CancellationToken.None).ConfigureAwait(False)
            Dim sb As New StringBuilder()
            If Not String.IsNullOrWhiteSpace(c.Summary) Then sb.AppendLine("Summary of the earlier conversation:").AppendLine(c.Summary).AppendLine()
            For Each m In c.Messages.Skip(Math.Max(0, c.Messages.Count - maxMessages))
                Dim tools = If(m.ToolCalls.Count = 0, "", " [tools: " & String.Join(", ", m.ToolCalls.Select(Function(t) Jx.StrOf(t, "name")).Distinct().Take(8)) & "]")
                sb.Append(If(m.Role = "user", "User: ", "Assistant: ")).Append(ToolKit.OneLine(If(m.Content, ""), 2000)).AppendLine(tools)
            Next
            Return sb.ToString()
        End Function

        ''' <summary>/btw: a quick question about this session; the answer is shown here and not added to the conversation.</summary>
        Private Async Function BtwAsync(question As String) As Task
            If question.Length = 0 Then
                Out.Note("Usage: /btw <question> — ask about this session without adding it to the conversation.")
                Return
            End If
            Out.StartSpinner("Answering on the side…")
            Try
                Dim digest = Await ConversationDigestAsync(16).ConfigureAwait(False)
                Dim messages As New List(Of Object) From {
                    New With {.role = "system", .content = "You answer a quick side question about the user's ongoing session with a coding agent. Be brief and direct. " &
                                                          "The conversation so far is given for context; your answer is shown only to the user and is not added to it."},
                    New With {.role = "user", .content = If(digest.Length > 0, "Conversation so far:" & vbLf & digest & vbLf, "") & "Side question: " & question}}
                Dim answer = Await _chat.CompleteAsync(SideModel(), messages, 1500, CancellationToken.None).ConfigureAwait(False)
                Out.StopSpinner()
                Out.Line(Ansi.Muted("by the way · not added to the conversation"))
                Out.Write(MarkdownStream.Render(If(answer.Length > 0, answer, "(no answer)")))
            Catch ex As ApiException
                Out.StopSpinner()
                Out.Fail("The side question failed: " & ex.Message)
            End Try
        End Function

        ''' <summary>/recap: a one-line summary of the session.</summary>
        Private Async Function RecapAsync() As Task
            If String.IsNullOrEmpty(State.ChatId) Then
                Out.Note("Nothing to recap yet.")
                Return
            End If
            Out.StartSpinner("Summarizing…")
            Try
                Dim digest = Await ConversationDigestAsync(30).ConfigureAwait(False)
                Dim messages As New List(Of Object) From {
                    New With {.role = "system", .content = "Summarize a coding session in ONE line of at most 30 words: what the user wants, what was done, and where it stands now. No preamble."},
                    New With {.role = "user", .content = digest}}
                Dim line = Await _chat.CompleteAsync(SideModel(), messages, 200, CancellationToken.None).ConfigureAwait(False)
                Out.StopSpinner()
                Out.Line(Ansi.Accent("※ ") & ToolKit.OneLine(If(line.Length > 0, line, "(no summary)"), 400))
            Catch ex As ApiException
                Out.StopSpinner()
                Out.Fail("The recap failed: " & ex.Message)
            End Try
        End Function

        ''' <summary>/branch [name] (/fork): copies the conversation as it is now and continues in the copy; the original stays.</summary>
        Private Async Function BranchAsync(name As String) As Task
            If String.IsNullOrEmpty(State.ChatId) Then
                Out.Note("Start the conversation first.")
                Return
            End If
            Dim original = State.ChatId
            Try
                Dim b = Await _chat.ForkAsync(original, If(name.Length > 0, name, Nothing), CancellationToken.None).ConfigureAwait(False)
                If Await OpenConversationAsync(b.Id, False).ConfigureAwait(False) Then
                    Out.Success($"Branched into ""{b.Title}"" ({b.Messages} message{If(b.Messages = 1, "", "s")}); you continue here.")
                    Out.Line(Ansi.Muted("  The original stays as it was: /resume " & original))
                End If
            Catch ex As ApiException
                Out.Fail(If(ex.IsNotFound, "This server cannot branch conversations yet (update the server).", "Could not branch: " & ex.Message))
            End Try
        End Function

        ''' <summary>/diff [path]: the working tree's changes (staged, unstaged and new files) as git sees them.</summary>
        Private Async Function DiffAsync(target As String) As Task
            Dim pathArg = If(target.Length > 0, " -- """ & target.Trim().Trim(""""c).Replace("""", "") & """", "")
            Dim inRepo = Await State.Shells.RunAsync("git rev-parse --is-inside-work-tree", State.Cwd, 15000, CancellationToken.None, stopAtWait:=True).ConfigureAwait(False)
            If inRepo.ExitCode <> 0 Then
                Out.Note("Not a git repository: /diff shows git's view of the changes.")
                Return
            End If
            Dim status = Await State.Shells.RunAsync("git status --short --untracked-files=all" & pathArg, State.Cwd, 30000, CancellationToken.None, stopAtWait:=True).ConfigureAwait(False)
            If String.IsNullOrWhiteSpace(status.Output) Then
                Out.Note("No changes in the working tree.")
                Return
            End If
            Out.Line(Ansi.Bold("Changes") & Ansi.Muted("  (git status)"))
            For Each l In status.Output.Replace(vbCr, "").Split(ChrW(10)).Where(Function(x) x.Trim().Length > 0)
                Dim code = If(l.Length >= 2, l.Substring(0, 2), l)
                Dim color = If(code.Contains("?"c) OrElse code.Contains("A"c), Ansi.Ok(l), If(code.Contains("D"c), Ansi.Bad(l), Ansi.Warn(l)))
                Out.Line("  " & color)
            Next
            ' Changes against the last commit (staged and unstaged together); a repository without commits shows the index.
            Dim hasHead = (Await State.Shells.RunAsync("git rev-parse --verify HEAD", State.Cwd, 15000, CancellationToken.None, stopAtWait:=True).ConfigureAwait(False)).ExitCode = 0
            Dim diff = Await State.Shells.RunAsync(If(hasHead, "git diff HEAD --no-color", "git diff --cached --no-color") & pathArg, State.Cwd, 60000, CancellationToken.None, stopAtWait:=True).ConfigureAwait(False)
            Dim lines = diff.Output.Replace(vbCr, "").Split(ChrW(10))
            Out.Line()
            Dim shown = 0
            For Each l In lines
                If shown >= 400 Then
                    Out.Line(Ansi.Muted($"  … {lines.Length - shown} more lines (run git diff for all of it)"))
                    Exit For
                End If
                shown += 1
                If l.StartsWith("diff --git", StringComparison.Ordinal) Then
                    Out.Line(Ansi.Bold(l))
                ElseIf l.StartsWith("+++", StringComparison.Ordinal) OrElse l.StartsWith("---", StringComparison.Ordinal) OrElse l.StartsWith("index ", StringComparison.Ordinal) Then
                    Out.Line(Ansi.Muted(l))
                ElseIf l.StartsWith("@@", StringComparison.Ordinal) Then
                    Out.Line(Ansi.Info(l))
                ElseIf l.StartsWith("+", StringComparison.Ordinal) Then
                    Out.Line(Ansi.Ok(l))
                ElseIf l.StartsWith("-", StringComparison.Ordinal) Then
                    Out.Line(Ansi.Bad(l))
                Else
                    Out.Line(l)
                End If
            Next
        End Function

        ''' <summary>/cd [path]: moves the session to another folder; the conversation continues and the tools work there.</summary>
        Private Sub ChangeDir(p As String)
            If p.Length = 0 Then
                Out.Line(State.Cwd)
                Return
            End If
            Dim full = State.Resolve(p.Trim().Trim(""""c))
            If Not Directory.Exists(full) Then
                Out.Fail("No such folder: " & full)
                Return
            End If
            Dim before = State.Cwd
            State.Cwd = full
            If Not State.WorkingDirs().Any(Function(d) Paths.IsInside(full, d)) Then State.ExtraDirs.Add(full)
            State.Mcp.Roots = State.WorkingDirs()
            State.Hooks.Run("CwdChanged", "", New Dictionary(Of String, Object) From {{"old_cwd", before}, {"new_cwd", full}})
            Out.Success("Working folder: " & full)
        End Sub

        ''' <summary>/release-notes: the newest CLI version the server hands out and what changed in it.</summary>
        Private Async Function ReleaseNotesAsync() As Task
            Try
                Dim info = Await Updater.CheckAsync(_api, CancellationToken.None).ConfigureAwait(False)
                Out.Line(Ansi.Bold("Agentic CLI ") & AppInfo.Version & Ansi.Muted(" (this copy)"))
                If info Is Nothing OrElse String.IsNullOrWhiteSpace(info.Version) Then
                    Out.Note("The server publishes no CLI release notes.")
                    Return
                End If
                Out.Line(Ansi.Bold("Latest on the server: ") & info.Version & If(info.Available, Ansi.Warn("  (update available: agentic update)"), Ansi.Muted("  (up to date)")))
                Out.Write(MarkdownStream.Render(If(String.IsNullOrWhiteSpace(info.Notes), "_No notes for this version._", info.Notes)))
            Catch ex As ApiException
                Out.Fail("Could not read the release notes: " & ex.Message)
            End Try
        End Function

        ''' <summary>/statusline [command|off]: the command whose output shows in the status line (settings: statusLine.command).</summary>
        Private Sub StatusLineCommand(arg As String)
            If arg.Length = 0 Then
                Dim current = State.Settings.Str("statusLine.command", Nothing)
                Out.Line(If(String.IsNullOrWhiteSpace(current), Ansi.Muted("No status line command. /statusline <command> sets one; it gets the session as JSON on stdin."),
                            Ansi.Muted("Status line command: ") & current))
                Return
            End If
            If arg.Equals("off", StringComparison.OrdinalIgnoreCase) OrElse arg.Equals("clear", StringComparison.OrdinalIgnoreCase) Then
                State.Settings.SetValue("user", "statusLine", Nothing)
                _statusLineCache = (Nothing, DateTime.MinValue)
                Out.Success("Status line removed.")
                Return
            End If
            State.Settings.SetValue("user", "statusLine", New JsonObject From {{"type", "command"}, {"command", arg}})
            _statusLineCache = (Nothing, DateTime.MinValue)
            Out.Success("Status line command saved in your user settings: " & arg)
        End Sub

        ''' <summary>/reload-skills: picks up skills and commands added or changed on disk during the session.</summary>
        Private Sub ReloadSkills()
            State.Skills = New Skills.SkillStore(State.ProjectDir, _includeClaude) With {.Disabled = _opts.DisableSlashCommands}
            Dim all = State.Skills.All()
            Out.Success($"Skills and commands reloaded: {all.Where(Function(s) s.Kind = "skill").Count()} skill(s), {all.Where(Function(s) s.Kind <> "skill").Count()} command(s).")
        End Sub

    End Class

End Namespace
