Option Strict On
Option Explicit On
Option Infer On

Imports System.IO
Imports System.Text
Imports System.Threading
Imports AgenticCli.Agent
Imports AgenticCli.Core
Imports AgenticCli.Tools
Imports AgenticCli.Ui

''' <summary>agentic --selftest: offline checks of the editor, tools, permissions, diffs, Markdown and settings.</summary>
Partial Friend NotInheritable Class SelfTest

    Private Sub New()
    End Sub

    Private Shared _fails As Integer
    Private Shared _passes As Integer

    Private Shared Sub Check(name As String, ok As Boolean, Optional detail As String = "")
        If ok Then
            _passes += 1
            Console.WriteLine("PASS " & name)
        Else
            _fails += 1
            Console.WriteLine("FAIL " & name & If(detail.Length > 0, " — " & detail, ""))
        End If
    End Sub

    Public Shared Function Run() As Integer
        Dim tmp = Path.Combine(Path.GetTempPath(), "agentic-selftest-" & Guid.NewGuid().ToString("N").Substring(0, 8))
        Directory.CreateDirectory(tmp)
        Try
            Ansi.InitForTest(False, True)
            EditorTests()
            VimAndIdleTests()
            MoreFeatureTests(tmp)
            MonitorTests(tmp)
            PlanGaugeTests()
            ProjectRunTests(tmp)
            KeyBindingTests(tmp)
            ScreenTests()
            Ansi.InitForTest(False, True)
            DiffTests()
            MarkdownTests()
            Dim state = NewState(tmp)
            RunSync(FileToolTests(state, tmp))
            RunSync(SearchTests(state, tmp))
            PermissionTests(state)
            RunSync(ShellTests(state, tmp))
            SettingsTests(tmp)
            MiscTests(tmp)
            ClaudeParityTests(tmp)
            Ansi.InitForTest(False, True)
            SyncUiTests()
            RunSync(SyncLinkTests(tmp))
            RunSync(ParallelToolTests(tmp))
            RunSync(McpTests(tmp))
            SubagentViewTests(tmp)
            RunSync(LinkTargetTests())
            RunSync(VoiceTests(tmp))
            RunSync(NavTests(tmp))
            Check("goal: the automatic continuation is named as such (not another window)",
                  TurnRunner.AutoGoalLine("↻ Keep working on the goal until it is met (automatic continuation 2 of 25: you stopped before reporting it met).") =
                  "Goal not met yet — continuing by itself (2 of 25)", TurnRunner.AutoGoalLine("↻ Keep working on the goal until it is met (automatic continuation 2 of 25: x)"))
        Finally
            Try
                Directory.Delete(tmp, True)
            Catch ex As IOException
            End Try
        End Try
        Console.WriteLine($"SELFTEST: {_passes} passed, {_fails} failed")
        Return If(_fails = 0, 0, 1)
    End Function

    Private Shared Sub RunSync(t As Task)
        t.GetAwaiter().GetResult()
    End Sub

    Private Shared Function NewState(dir As String) As AgentState
        Dim s As New AgentState With {.ProjectDir = dir, .Cwd = dir, .Settings = Settings.Load(dir), .Project = ProjectInfo.Detect(dir), .Interactive = False}
        s.Shells = New ShellManager("auto")
        s.Checkpoints = New CheckpointStore(Path.Combine(dir, ".checkpoints"))
        s.Checkpoints.BeginTurn(1, "test")
        Return s
    End Function

    Private Shared Function Run(tool As ITool, json As String, state As AgentState) As ToolResult
        Return tool.RunAsync(Jx.Parse(json), New ToolContext With {.State = state, .Cancel = CancellationToken.None}).GetAwaiter().GetResult()
    End Function

    Private Shared Function J(o As Object) As String
        Return Jx.Stringify(o)
    End Function

    ' ------------------------------------------------------------------ the message box

    Private Shared Function Edit(setup As Action(Of FakeTerminal), Optional history As List(Of String) = Nothing) As (Text As String, Ed As LineEditor)
        Dim t As New FakeTerminal()
        setup(t)
        Dim ed As New LineEditor(t) With {.History = If(history, New List(Of String))}
        Return (ed.ReadLine(), ed)
    End Function

    ''' <summary>Vim keys in the box (/vim), and loops / the heartbeat taking over an empty box (IdleTick).</summary>
    Private Shared Sub VimAndIdleTests()
        Dim vim = Function(setup As Action(Of FakeTerminal)) As String
                      Dim t As New FakeTerminal()
                      setup(t)
                      Return New LineEditor(t) With {.VimMode = True}.ReadLine()
                  End Function
        Dim esc = Sub(t As FakeTerminal) t.Press(ConsoleKey.Escape, ch:=ChrW(27))
        Check("vim: Esc, 0, dw deletes the first word", vim(Sub(t)
                                                              t.Type("hello world")
                                                              esc(t)
                                                              t.Type("0dw" & vbLf)
                                                          End Sub) = "world")
        Check("vim: b, cw changes a word", vim(Sub(t)
                                                   t.Type("one two three")
                                                   esc(t)
                                                   t.Type("bcw3" & vbLf)
                                               End Sub) = "one two 3")
        Check("vim: x deletes under the cursor, A appends", vim(Sub(t)
                                                                    t.Type("abc")
                                                                    esc(t)
                                                                    t.Type("xAd" & vbLf)
                                                                End Sub) = "abd")
        Check("vim: dd clears the line, u undoes it", vim(Sub(t)
                                                              t.Type("keep me")
                                                              esc(t)
                                                              t.Type("ddu" & vbLf)
                                                          End Sub) = "keep me")
        Check("vim: yw then P pastes the word", vim(Sub(t)
                                                        t.Type("go now")
                                                        esc(t)
                                                        t.Type("0ywP" & vbLf)
                                                    End Sub) = "go go now")
        Check("vim: off by default (letters are text)", Edit(Sub(t)
                                                                 t.Type("ab")
                                                                 esc(t)
                                                                 t.Type("x" & vbLf)
                                                             End Sub).Text = "abx")
        ' A due loop or heartbeat takes over the empty box.
        Dim idle As New FakeTerminal()
        Dim asked = 0
        Dim ed As New LineEditor(idle) With {.IdleTick = Function()
                                                              asked += 1
                                                              Return If(asked >= 2, "loop prompt", Nothing)
                                                          End Function}
        Check("idle: a due prompt is submitted from an empty box", ed.ReadLine() = "loop prompt" AndAlso ed.LastWasIdle AndAlso asked = 2, $"asked {asked}")
        Dim typed As New FakeTerminal()
        typed.Type("mine" & vbLf)
        Dim ed2 As New LineEditor(typed) With {.IdleTick = Function() "loop prompt"}
        Check("idle: typing wins over a waiting loop", ed2.ReadLine() = "mine" AndAlso Not ed2.LastWasIdle)
        ' Intervals for /loop and /heartbeat.
        Check("loop: intervals", Agent.Session.ParseInterval("5m").GetValueOrDefault() = TimeSpan.FromMinutes(5) AndAlso Agent.Session.ParseInterval("1h30m").GetValueOrDefault() = TimeSpan.FromMinutes(90) AndAlso
                                 Agent.Session.ParseInterval("45s").GetValueOrDefault() = TimeSpan.FromSeconds(45) AndAlso Agent.Session.ParseInterval("20").GetValueOrDefault() = TimeSpan.FromMinutes(20) AndAlso
                                 Not Agent.Session.ParseInterval("soon").HasValue AndAlso Not Agent.Session.ParseInterval("0m").HasValue AndAlso
                                 Agent.Session.FormatInterval(TimeSpan.FromMinutes(90)) = "1h30m" AndAlso Agent.Session.FormatInterval(TimeSpan.FromSeconds(30)) = "30s")
    End Sub

    Private Shared Sub EditorTests()
        Check("editor: typing and Enter", Edit(Sub(t) t.Type("hello" & vbLf)).Text = "hello")
        Check("editor: backspace", Edit(Sub(t)
                                            t.Type("helo")
                                            t.Press(ConsoleKey.Backspace)
                                            t.Type("lo" & vbLf)
                                        End Sub).Text = "helo".Substring(0, 3) & "lo")
        Check("editor: left arrow and insert", Edit(Sub(t)
                                                        t.Type("ac")
                                                        t.Press(ConsoleKey.LeftArrow)
                                                        t.Type("b" & vbLf)
                                                    End Sub).Text = "abc")
        Dim multi = Edit(Sub(t)
                             t.Type("line one\")
                             t.Press(ConsoleKey.Enter, ch:=ChrW(13))
                             t.Keys.Enqueue(New ConsoleKeyInfo(ChrW(0), ConsoleKey.F24, False, False, False)) ' a pause: not part of a paste
                             t.Type("line two" & vbLf)
                         End Sub).Text
        Check("editor: backslash-Enter makes a new line", multi = "line one" & vbLf & "line two", multi.Replace(vbLf, "⏎"))
        Check("editor: Ctrl+J new line", Edit(Sub(t)
                                                  t.Type("a")
                                                  t.Press(ConsoleKey.J, ctrl:=True, ch:=ChrW(10))
                                                  t.Type("b" & vbLf)
                                              End Sub).Text = "a" & vbLf & "b")
        Check("editor: history with Up", Edit(Sub(t)
                                                  t.Press(ConsoleKey.UpArrow)
                                                  t.Press(ConsoleKey.Enter, ch:=ChrW(13))
                                              End Sub, New List(Of String) From {"first", "second"}).Text = "second")
        Check("editor: Ctrl+U clears the line", Edit(Sub(t)
                                                         t.Type("junk")
                                                         t.Press(ConsoleKey.U, ctrl:=True)
                                                         t.Type("ok" & vbLf)
                                                     End Sub).Text = "ok")
        Check("editor: Ctrl+W deletes a word", Edit(Sub(t)
                                                        t.Type("one two")
                                                        t.Press(ConsoleKey.W, ctrl:=True)
                                                        t.Type(vbLf)
                                                    End Sub).Text = "one ")
        Check("editor: Ctrl+Z undo", Edit(Sub(t)
                                              t.Type("keep")
                                              t.Press(ConsoleKey.U, ctrl:=True)
                                              t.Press(ConsoleKey.Z, ctrl:=True)
                                              t.Type(vbLf)
                                          End Sub).Text = "keep")
        Dim d = Edit(Sub(t) t.Press(ConsoleKey.D, ctrl:=True, ch:=ChrW(4)))
        Check("editor: Ctrl+D on empty exits", d.Text Is Nothing AndAlso d.Ed.ExitRequested)
        ' A big paste becomes a placeholder and is expanded on Enter.
        Dim big = String.Join(vbLf, Enumerable.Range(1, 30).Select(Function(n) "pasted line " & n))
        Dim pasted = Edit(Sub(t)
                              t.Type(big)
                              t.Keys.Enqueue(New ConsoleKeyInfo(ChrW(0), ConsoleKey.F24, False, False, False))
                              t.Type(vbLf)
                          End Sub)
        Check("editor: big paste collapses and expands", pasted.Text = big AndAlso pasted.Ed.Pastes.Count = 1, If(pasted.Text, "(nothing)").Length & " chars")
        ' Completion: Tab accepts the suggestion.
        Dim t2 As New FakeTerminal()
        t2.Type("/he")
        t2.Press(ConsoleKey.Tab)
        t2.Type(vbLf)
        Dim ed2 As New LineEditor(t2) With {.Suggest = Function(b, p) (New List(Of Suggestion) From {New Suggestion With {.Text = "/help", .Label = "/help", .Submit = True}}, 0)}
        Check("editor: Tab completes a command", ed2.ReadLine() = "/help")
        ' Enter on a typed command runs it at once (no second Enter), also for commands that take arguments.
        For Each typed In {"/model", "/mo"}
            Dim t3 As New FakeTerminal()
            t3.Type(typed)
            t3.Press(ConsoleKey.Enter, ch:=ChrW(13))
            Dim ed3 As New LineEditor(t3) With {.Suggest = Function(b, p) (New List(Of Suggestion) From {New Suggestion With {.Text = "/model ", .Label = "/model [model]", .Submit = True}}, 0)}
            Dim got = ed3.ReadLine()
            Check("editor: Enter runs " & typed, got IsNot Nothing AndAlso got.Trim() = "/model", If(got, "(nothing)"))
        Next
    End Sub

    ' ------------------------------------------------------------------ redraws, checked on a virtual screen

    ''' <summary>A minimal VT100 screen: enough of the escape sequences the editor and menus use.</summary>
    Private NotInheritable Class VtScreen
        Private ReadOnly _rows As New List(Of Char())
        Private ReadOnly _w As Integer
        Private _r, _c As Integer

        Public Sub New(width As Integer)
            _w = width
        End Sub

        Private Function Row(i As Integer) As Char()
            While _rows.Count <= i
                _rows.Add(Enumerable.Repeat(" "c, _w).ToArray())
            End While
            Return _rows(i)
        End Function

        Public Sub Feed(text As String)
            Dim i = 0
            While i < text.Length
                Dim ch = text(i)
                If ch = ChrW(27) AndAlso i + 1 < text.Length AndAlso text(i + 1) = "["c Then
                    Dim j = i + 2
                    While j < text.Length AndAlso Not (text(j) >= "@"c AndAlso text(j) <= "~"c)
                        j += 1
                    End While
                    Dim args = text.Substring(i + 2, j - i - 2).TrimStart("?"c)
                    Dim n = 1
                    Integer.TryParse(args.Split(";"c)(0), n)
                    If n = 0 Then n = 1
                    Select Case text(j)
                        Case "A"c : _r = Math.Max(0, _r - n)
                        Case "B"c : _r += n
                        Case "C"c : _c = Math.Min(_w - 1, _c + n)
                        Case "D"c : _c = Math.Max(0, _c - n)
                        Case "G"c : _c = n - 1
                        Case "H"c : _r = 0 : _c = 0
                        Case "J"c
                            If args = "2" OrElse args = "3" Then
                                _rows.Clear()
                            Else
                                Dim cur = Row(_r)
                                For k = _c To _w - 1 : cur(k) = " "c : Next
                                For rr = _r + 1 To _rows.Count - 1
                                    _rows(rr) = Enumerable.Repeat(" "c, _w).ToArray()
                                Next
                            End If
                        Case "K"c
                            Dim cur = Row(_r)
                            For k = 0 To _w - 1 : cur(k) = " "c : Next
                    End Select
                    i = j + 1
                    Continue While
                End If
                If ch = ChrW(27) AndAlso i + 1 < text.Length AndAlso text(i + 1) = "]"c Then
                    Dim bel = text.IndexOf(ChrW(7), i)
                    i = If(bel < 0, text.Length, bel + 1)
                    Continue While
                End If
                Select Case ch
                    Case ChrW(13) : _c = 0
                    Case ChrW(10) : _r += 1 : _c = 0
                    Case ChrW(7)
                    Case Else
                        If _c >= _w Then
                            _r += 1
                            _c = 0
                        End If
                        Row(_r)(_c) = ch
                        _c += 1
                End Select
                i += 1
            End While
        End Sub

        Public Function Lines() As List(Of String)
            Return _rows.Select(Function(r) New String(r).TrimEnd()).ToList()
        End Function
    End Class

    Private Shared Sub ScreenTests()
        Ansi.InitForTest(False, False) ' no colours (NO_COLOR) must still redraw in place
        ' Typing key by key redraws the box in place: one box on screen, holding the latest text.
        Dim t As New FakeTerminal() With {.Width = 60}
        For Each c In "hello"
            t.Type(c.ToString())
            t.Keys.Enqueue(New ConsoleKeyInfo(ChrW(0), ConsoleKey.F24, False, False, False))
        Next
        t.Type("/he")
        t.Keys.Enqueue(New ConsoleKeyInfo(ChrW(0), ConsoleKey.F22, False, False, False))
        Dim screenAfterTyping As List(Of String) = Nothing
        Dim ed As New LineEditor(t) With {.Footer = Function() "? for shortcuts", .FooterRight = Function() "model",
            .Suggest = Function(b, p) (If(b.StartsWith("/"), New List(Of Suggestion) From {New Suggestion With {.Text = "/help", .Label = "/help", .Hint = "Show help"}}, New List(Of Suggestion)), 0),
            .OnKey = Function(k)
                         Return False
                     End Function}
        ' Read until Ctrl+D (end of the script) and look at the screen before the box is cleared.
        Dim captured = ed.ReadLine()
        Dim vt As New VtScreen(60)
        Dim all = t.Output.ToString()
        ' The screen as it was when the mark key was read (after typing).
        vt.Feed(all.Substring(0, If(t.Marks.Count > 0, t.Marks(0), all.Length)))
        screenAfterTyping = vt.Lines()
        Dim tops = screenAfterTyping.Where(Function(l) l.StartsWith("┌")).Count()
        Check("screen: the box is redrawn in place (one box)", tops = 1, String.Join(" ⏎ ", screenAfterTyping.Where(Function(l) l.Length > 0)))
        Check("screen: the box shows the latest text and suggestions", screenAfterTyping.Any(Function(l) l.Contains("> hello/he")),
              String.Join(" ⏎ ", screenAfterTyping.Where(Function(l) l.Length > 0)))
        Dim widths = all.Substring(0, If(t.Marks.Count > 0, t.Marks(0), all.Length)).Split({vbCrLf}, StringSplitOptions.None).Select(Function(l) Ansi.Width(l)).ToList()
        Check("screen: no line is wider than the terminal", screenAfterTyping.All(Function(l) Not l.StartsWith(" │")), "widths " & String.Join(",", widths.Take(12)))
        ' Suggestions appear under the box for a slash command and go away when they no longer match.
        Dim st As New FakeTerminal() With {.Width = 60}
        For Each c In "/he"
            st.Type(c.ToString())
            st.Keys.Enqueue(New ConsoleKeyInfo(ChrW(0), ConsoleKey.F24, False, False, False))
        Next
        st.Keys.Enqueue(New ConsoleKeyInfo(ChrW(0), ConsoleKey.F22, False, False, False))
        st.Press(ConsoleKey.Backspace)
        st.Press(ConsoleKey.Backspace)
        st.Press(ConsoleKey.Backspace)
        st.Type("x")
        st.Keys.Enqueue(New ConsoleKeyInfo(ChrW(0), ConsoleKey.F22, False, False, False))
        Dim sed As New LineEditor(st) With {.Footer = Function() "? for shortcuts",
            .Suggest = Function(b, p) (If(b.StartsWith("/"), New List(Of Suggestion) From {New Suggestion With {.Text = "/help", .Label = "/help", .Hint = "Show help"}}, New List(Of Suggestion)), 0)}
        sed.ReadLine()
        Dim so = st.Output.ToString()
        Dim s1 As New VtScreen(60)
        s1.Feed(so.Substring(0, st.Marks(0)))
        Dim s2 As New VtScreen(60)
        s2.Feed(so.Substring(0, st.Marks(1)))
        Check("screen: suggestions show and then clear", s1.Lines().Any(Function(l) l.Contains("/help") AndAlso l.Contains("Show help")) AndAlso Not s2.Lines().Any(Function(l) l.Contains("Show help")) AndAlso s2.Lines().Any(Function(l) l.Contains("> x")),
              String.Join(" ⏎ ", s2.Lines().Where(Function(l) l.Length > 0)))
        Dim vt2 As New VtScreen(60)
        vt2.Feed(all)
        Check("screen: the box is gone after it closes", vt2.Lines().All(Function(l) l.Trim().Length = 0), String.Join(" ⏎ ", vt2.Lines().Where(Function(l) l.Length > 0)))

        ' A menu moves its pointer in place and leaves one line with the answer.
        Dim mt As New FakeTerminal() With {.Width = 60}
        mt.Press(ConsoleKey.DownArrow)
        mt.Keys.Enqueue(New ConsoleKeyInfo(ChrW(0), ConsoleKey.F24, False, False, False))
        mt.Press(ConsoleKey.Enter, ch:=ChrW(13))
        Dim saved = Menu.Term
        Menu.Term = mt
        Dim pick As Integer
        Try
            pick = Menu.Select(" Do you want to proceed?", {New MenuOption("Yes"), New MenuOption("No")})
        Finally
            Menu.Term = saved
        End Try
        Dim vt3 As New VtScreen(60)
        vt3.Feed(mt.Output.ToString())
        Dim menuLines = vt3.Lines().Where(Function(l) l.Trim().Length > 0).ToList()
        Check("screen: a menu collapses to its answer", pick = 1 AndAlso menuLines.Count = 1 AndAlso menuLines(0).Contains("Do you want to proceed?") AndAlso menuLines(0).Contains("No"), String.Join(" ⏎ ", menuLines))

        ' A long list (like /model): type to filter, scrolls inside a short terminal, never taller than it.
        Dim many = Enumerable.Range(0, 300).Select(Function(n) New MenuOption("model-" & n, "vendor" & (n Mod 7), "id" & n)).ToList()
        Dim lt As New FakeTerminal() With {.Width = 80, .Height = 24}
        lt.Type("17")
        lt.Keys.Enqueue(New ConsoleKeyInfo(ChrW(0), ConsoleKey.F22, False, False, False))
        lt.Press(ConsoleKey.DownArrow)
        lt.Press(ConsoleKey.Enter, ch:=ChrW(13))
        Menu.Term = lt
        Dim longPick As Integer
        Try
            longPick = Menu.Select(" Select a model", many, 250)
        Finally
            Menu.Term = saved
        End Try
        Dim lv As New VtScreen(80)
        lv.Feed(lt.Output.ToString().Substring(0, lt.Marks(0)))
        Dim shown = lv.Lines().Where(Function(l) l.Trim().Length > 0).ToList()
        Check("menu: filtering a long list picks the right item", longPick = 117, "picked " & longPick)
        Check("menu: a filtered list fits the terminal and says how many match", shown.Count <= 24 AndAlso shown.Any(Function(l) l.Contains("17") AndAlso l.Contains("of 300")) AndAlso
              shown.Any(Function(l) l.Contains("model-17")) AndAlso shown.Any(Function(l) l.Contains("more")), String.Join(" ⏎ ", shown))
        ' Without filtering, PageDown scrolls a window that stays inside the screen.
        Dim pt As New FakeTerminal() With {.Width = 80, .Height = 20}
        pt.Press(ConsoleKey.PageDown)
        pt.Press(ConsoleKey.PageDown)
        pt.Keys.Enqueue(New ConsoleKeyInfo(ChrW(0), ConsoleKey.F22, False, False, False))
        pt.Press(ConsoleKey.Enter, ch:=ChrW(13))
        Menu.Term = pt
        Dim pagePick As Integer
        Try
            pagePick = Menu.Select(" Pick", many.Take(60).ToList(), 0, filterable:=False)
        Finally
            Menu.Term = saved
        End Try
        Dim pv As New VtScreen(80)
        pv.Feed(pt.Output.ToString().Substring(0, pt.Marks(0)))
        Dim pshown = pv.Lines().Where(Function(l) l.Trim().Length > 0).ToList()
        Check("menu: PageDown scrolls a window inside the screen", pagePick > 10 AndAlso pshown.Count <= 20 AndAlso pshown.Any(Function(l) l.Contains("↑")) AndAlso
              pshown.Any(Function(l) l.Contains("model-" & pagePick)), $"picked {pagePick}: " & String.Join(" ⏎ ", pshown))
        Dim fv As New VtScreen(80)
        fv.Feed(pt.Output.ToString())
        Check("menu: a long list collapses to one line", fv.Lines().Where(Function(l) l.Trim().Length > 0).Count() = 1, String.Join(" ⏎ ", fv.Lines().Where(Function(l) l.Trim().Length > 0)))
    End Sub

    ' ------------------------------------------------------------------ diff and Markdown

    Private Shared Sub DiffTests()
        Dim d = Diff.Compute("a" & vbLf & "b" & vbLf & "c", "a" & vbLf & "B" & vbLf & "c" & vbLf & "d")
        Dim c = Diff.Counts(d)
        Check("diff: counts additions and removals", c.Added = 2 AndAlso c.Removed = 1, $"+{c.Added} -{c.Removed}")
        Check("diff: keeps unchanged lines in order", d.First().Text = "a" AndAlso d.First().Kind = DiffKind.Same)
        Dim big = Diff.Compute(String.Join(vbLf, Enumerable.Range(1, 500)), String.Join(vbLf, Enumerable.Range(1, 500).Select(Function(n) If(n = 250, "x", n.ToString()))))
        Check("diff: one changed line in 500", Diff.Counts(big).Added = 1 AndAlso Diff.Counts(big).Removed = 1)
    End Sub

    Private Shared Sub MarkdownTests()
        Dim plain = MarkdownStream.Render("# Title" & vbLf & "- item **bold**" & vbLf & "```vb" & vbLf & "Dim x = 1" & vbLf & "```" & vbLf & "| a | b |" & vbLf & "|---|---|" & vbLf & "| 1 | 2 |")
        Check("markdown: heading, list, code, table", plain.Contains("Title") AndAlso plain.Contains("• item bold") AndAlso plain.Contains("Dim x = 1") AndAlso plain.Contains("│ a ") AndAlso plain.Contains("┌"), plain.Replace(vbLf, "⏎"))
        Dim sb As New StringBuilder()
        Dim m As New MarkdownStream(Sub(t) sb.Append(t), "")
        m.Append("hel")
        m.Append("lo wor")
        m.Append("ld" & vbLf & "next")
        m.Finish()
        Check("markdown: streams in pieces", sb.ToString().Contains("hello world") AndAlso sb.ToString().Contains("next"))
        Check("markdown: wraps long lines", Ansi.WrapText(New String("x"c, 10) & " " & New String("y"c, 10), 12).Count = 2)
    End Sub

    ' ------------------------------------------------------------------ tools

    Private Shared Async Function FileToolTests(state As AgentState, dir As String) As Task
        Dim f = Path.Combine(dir, "a.txt")
        Dim w = Run(New WriteTool(), J(New With {.file_path = f, .content = "one" & vbLf & "two" & vbLf & "three" & vbLf}), state)
        Check("write: creates a file", w.Ok AndAlso File.ReadAllText(f) = "one" & vbLf & "two" & vbLf & "three" & vbLf, w.Content)
        Dim r = Run(New ReadTool(), J(New With {.file_path = "a.txt"}), state)
        Check("read: numbered lines", r.Ok AndAlso r.Content.Contains("     2" & vbTab & "two"), r.Content)
        Dim e = Run(New EditTool(), J(New With {.file_path = f, .old_string = "two", .new_string = "2"}), state)
        Check("edit: replaces unique text", e.Ok AndAlso File.ReadAllText(f).Contains("2" & vbLf & "three"), e.Content)
        Dim e2 = Run(New EditTool(), J(New With {.file_path = f, .old_string = "missing", .new_string = "x"}), state)
        Check("edit: reports text not found", Not e2.Ok AndAlso e2.Content.Contains("not found"))
        File.WriteAllText(f, "x" & vbLf & "x" & vbLf)
        Dim e3 = Run(New EditTool(), J(New With {.file_path = f, .old_string = "x", .new_string = "y"}), state)
        Check("edit: refuses a file changed since it was read", Not e3.Ok AndAlso e3.Content.Contains("modified since read"), e3.Content)
        Run(New ReadTool(), J(New With {.file_path = f}), state)
        Dim e4 = Run(New EditTool(), J(New With {.file_path = f, .old_string = "x", .new_string = "y"}), state)
        Check("edit: refuses an ambiguous match", Not e4.Ok AndAlso e4.Content.Contains("2 matches"))
        Dim e5 = Run(New EditTool(), J(New With {.file_path = f, .old_string = "x", .new_string = "y", .replace_all = True}), state)
        Check("edit: replace_all", e5.Ok AndAlso File.ReadAllText(f) = "y" & vbLf & "y" & vbLf)
        ' CRLF files keep their line endings.
        Dim crlf = Path.Combine(dir, "w.txt")
        File.WriteAllText(crlf, "alpha" & vbCrLf & "beta" & vbCrLf)
        Run(New ReadTool(), J(New With {.file_path = crlf}), state)
        Dim e6 = Run(New EditTool(), J(New With {.file_path = crlf, .old_string = "alpha" & vbLf & "beta", .new_string = "alpha" & vbLf & "gamma"}), state)
        Check("edit: keeps CRLF line endings", e6.Ok AndAlso File.ReadAllText(crlf) = "alpha" & vbCrLf & "gamma" & vbCrLf, File.ReadAllText(crlf).Replace(vbCr, "\r").Replace(vbLf, "\n"))
        Dim me1 = Run(New MultiEditTool(), J(New With {.file_path = crlf, .edits = {New With {.old_string = "alpha", .new_string = "A"}, New With {.old_string = "gamma", .new_string = "G"}}}), state)
        Check("multiedit: applies edits in order", me1.Ok AndAlso File.ReadAllText(crlf) = "A" & vbCrLf & "G" & vbCrLf, me1.Content)
        Dim me2 = Run(New MultiEditTool(), J(New With {.file_path = crlf, .edits = {New With {.old_string = "A", .new_string = "B"}, New With {.old_string = "nope", .new_string = "x"}}}), state)
        Check("multiedit: all or nothing", Not me2.Ok AndAlso File.ReadAllText(crlf).StartsWith("A"))
        ' A file with mixed line endings changes only where it was edited.
        Dim mixed = Path.Combine(dir, "mixed.txt")
        File.WriteAllText(mixed, "a" & vbCrLf & "b" & vbLf & "c" & vbCrLf & "d" & vbLf)
        Run(New ReadTool(), J(New With {.file_path = mixed}), state)
        Dim e7 = Run(New EditTool(), J(New With {.file_path = mixed, .old_string = "c", .new_string = "C"}), state)
        Check("edit: mixed line endings stay as they were", e7.Ok AndAlso File.ReadAllText(mixed) = "a" & vbCrLf & "b" & vbLf & "C" & vbCrLf & "d" & vbLf,
              File.ReadAllText(mixed).Replace(vbCr, "\r").Replace(vbLf, "\n"))
        Check("line endings: new lines take the usual ending", LineEndings.Restore("x" & vbCrLf & "y" & vbCrLf & "z" & vbLf, "x" & vbLf & "new" & vbLf & "y" & vbLf & "z" & vbLf) =
              "x" & vbCrLf & "new" & vbCrLf & "y" & vbCrLf & "z" & vbLf)
        ' A picture is read as an image for the model.
        Dim png = Path.Combine(dir, "p.png")
        File.WriteAllBytes(png, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg=="))
        Dim pr = Run(New ReadTool(), J(New With {.file_path = png}), state)
        Check("read: a picture goes to the model as an image", pr.Ok AndAlso pr.ImageMime = "image/png" AndAlso pr.Image IsNot Nothing AndAlso pr.Image.Length > 20, pr.Content)
        ' Checkpoints restore what the tools changed.
        Dim restored = state.Checkpoints.RestoreFrom(1)
        Check("checkpoints: restore files changed in the turn", restored.Count >= 2 AndAlso Not File.Exists(f) AndAlso File.ReadAllText(crlf) = "alpha" & vbCrLf & "beta" & vbCrLf,
              String.Join(", ", restored))
        Dim ls = Run(New LsTool(), J(New With {.path = dir}), state)
        Check("ls: lists files", ls.Ok AndAlso ls.Content.Contains("w.txt"), ls.Content)
        Dim nbPath = Path.Combine(dir, "n.ipynb")
        File.WriteAllText(nbPath, "{""cells"":[{""cell_type"":""code"",""id"":""c1"",""metadata"":{},""source"":[""print(1)""],""outputs"":[]}],""metadata"":{},""nbformat"":4,""nbformat_minor"":5}")
        Dim nr = Run(New ReadTool(), J(New With {.file_path = nbPath}), state)
        Dim ne = Run(New NotebookEditTool(), J(New With {.notebook_path = nbPath, .cell_id = "c1", .new_source = "print(2)"}), state)
        Check("notebook: read and edit a cell", nr.Ok AndAlso nr.Content.Contains("print(1)") AndAlso ne.Ok AndAlso File.ReadAllText(nbPath).Contains("print(2)"), ne.Content)
        Await Task.CompletedTask
    End Function

    Private Shared Async Function SearchTests(state As AgentState, dir As String) As Task
        Directory.CreateDirectory(Path.Combine(dir, "src", "deep"))
        Directory.CreateDirectory(Path.Combine(dir, "node_modules", "x"))
        Directory.CreateDirectory(Path.Combine(dir, "build"))
        File.WriteAllText(Path.Combine(dir, "src", "app.vb"), "Module App" & vbLf & "  Sub Main()" & vbLf & "  End Sub" & vbLf & "End Module")
        File.WriteAllText(Path.Combine(dir, "src", "deep", "util.vb"), "Function Helper() As Integer" & vbLf & "  Return 42" & vbLf & "End Function")
        File.WriteAllText(Path.Combine(dir, "node_modules", "x", "skip.vb"), "Sub Main()")
        File.WriteAllText(Path.Combine(dir, "build", "out.vb"), "Sub Main()")
        File.WriteAllText(Path.Combine(dir, ".gitignore"), "build/" & vbLf)
        Directory.CreateDirectory(Path.Combine(dir, ".git"))
        Dim g = Run(New GlobTool(), J(New With {.pattern = "**/*.vb"}), state)
        Check("glob: finds files recursively, skips node_modules and .gitignore'd folders", g.Ok AndAlso g.Content.Contains("app.vb") AndAlso g.Content.Contains("util.vb") AndAlso
              Not g.Content.Contains("skip.vb") AndAlso Not g.Content.Contains("out.vb"), g.Content)
        Dim g2 = Run(New GlobTool(), J(New With {.pattern = "*.vb", .path = "src"}), state)
        Check("glob: no ** stays in the folder", g2.Content.Contains("app.vb") AndAlso Not g2.Content.Contains("util.vb"), g2.Content)
        Dim gr = Run(New GrepTool(), J(New With {.pattern = "Sub Main"}), state)
        Check("grep: files with matches", gr.Ok AndAlso gr.Content.Contains("app.vb") AndAlso Not gr.Content.Contains("skip.vb"), gr.Content)
        Dim gr2 = New GrepTool().RunAsync(Jx.Parse("{""pattern"":""return \\d+"",""-i"":true,""output_mode"":""content"",""-B"":1}"), New ToolContext With {.State = state, .Cancel = CancellationToken.None}).GetAwaiter().GetResult()
        Check("grep: content with context and -i", gr2.Content.Contains(":2:  Return 42") AndAlso gr2.Content.Contains("-1-Function Helper"), gr2.Content)
        Dim gr3 = Run(New GrepTool(), J(New With {.pattern = "Main", .type = "vb", .output_mode = "count"}), state)
        Check("grep: count mode with type filter", gr3.Content.Contains("app.vb:1"), gr3.Content)
        Await Task.CompletedTask
    End Function

    Private Shared Sub PermissionTests(state As AgentState)
        Dim p As New PermissionEngine(state)
        Dim bash As New BashTool(state.Shells)
        Dim ev = Function(t As ITool, json As String) p.Evaluate(t, Jx.Parse(json)).Verdict
        Check("permissions: reading inside the project is allowed", ev(New ReadTool(), J(New With {.file_path = "x.txt"})) = Verdict.Allow)
        Check("permissions: reading outside asks", ev(New ReadTool(), J(New With {.file_path = If(OperatingSystem.IsWindows(), "C:\Windows\win.ini", "/etc/hostname")})) = Verdict.Ask)
        Check("permissions: edits ask in default mode", ev(New EditTool(), J(New With {.file_path = "x.txt"})) = Verdict.Ask)
        state.Mode = PermissionMode.AcceptEdits
        Check("permissions: edits run in acceptEdits mode", ev(New EditTool(), J(New With {.file_path = "x.txt"})) = Verdict.Allow)
        state.Mode = PermissionMode.Plan
        Check("permissions: plan mode refuses edits", ev(New WriteTool(), J(New With {.file_path = "x.txt"})) = Verdict.Deny)
        state.Mode = PermissionMode.Default
        Check("permissions: read-only commands run", ev(bash, J(New With {.command = "git status && git diff --stat"})) = Verdict.Allow)
        Check("permissions: other commands ask", ev(bash, J(New With {.command = "npm install"})) = Verdict.Ask)
        Check("permissions: redirection asks", ev(bash, J(New With {.command = "echo hi > out.txt"})) = Verdict.Ask)
        p.SessionAllow.Add(PermissionRule.Parse("Bash(npm run test:*)", "test"))
        Check("permissions: prefix rule allows", ev(bash, J(New With {.command = "npm run test -- --watch"})) = Verdict.Allow)
        Check("permissions: prefix rule does not over-match", ev(bash, J(New With {.command = "npm run testx"})) = Verdict.Ask)
        Check("permissions: chained commands need every part allowed", ev(bash, J(New With {.command = "npm run test && rm -rf x"})) = Verdict.Ask)
        Check("permissions: prefix rule covers test:unit", ev(bash, J(New With {.command = "npm run test:unit"})) = Verdict.Allow)
        Check("permissions: a file named like a safe command asks", ev(bash, J(New With {.command = ".\cat.ps1 x"})) = Verdict.Ask AndAlso ev(bash, J(New With {.command = "C:\tmp\ls.exe"})) = Verdict.Ask)
        Check("permissions: env, sort -o, git log --output and git branch <name> ask",
              ev(bash, J(New With {.command = "env rm -rf x"})) = Verdict.Ask AndAlso ev(bash, J(New With {.command = "sort -o out.txt in.txt"})) = Verdict.Ask AndAlso
              ev(bash, J(New With {.command = "git log --output=x.txt"})) = Verdict.Ask AndAlso ev(bash, J(New With {.command = "git branch evil"})) = Verdict.Ask)
        Check("permissions: code inside PowerShell blocks asks",
              ev(bash, J(New With {.command = "Get-ChildItem | Where-Object { ri x }"})) = Verdict.Ask AndAlso ev(bash, J(New With {.command = "Write-Output (ri x)"})) = Verdict.Ask AndAlso
              ev(bash, J(New With {.command = "ls; [IO.File]::Delete('x')"})) = Verdict.Ask AndAlso ev(bash, J(New With {.command = ". .\x.ps1"})) = Verdict.Ask)
        Check("permissions: 2>&1, quoted brackets and listing stay read-only (2>file asks)",
              ev(bash, J(New With {.command = "git status 2>&1 | Select-String ""(x)"""})) = Verdict.Allow AndAlso ev(bash, J(New With {.command = "git branch -a"})) = Verdict.Allow AndAlso
              ev(bash, J(New With {.command = "git.exe log -5"})) = Verdict.Allow AndAlso ev(bash, J(New With {.command = "ls 2>out.txt"})) = Verdict.Ask)
        p.SessionDeny.Add(PermissionRule.Parse("Read(**/*.env)", "test"))
        Check("permissions: deny rule with a path pattern", ev(New ReadTool(), J(New With {.file_path = "config/.env"})) = Verdict.Deny)
        p.SessionAllow.Add(PermissionRule.Parse("Edit(src/**)", "test"))
        Check("permissions: Edit rule covers Write in its folder", ev(New WriteTool(), J(New With {.file_path = "src/a.vb"})) = Verdict.Allow AndAlso ev(New WriteTool(), J(New With {.file_path = "lib/a.vb"})) = Verdict.Ask)
        p.SessionAllow.Add(PermissionRule.Parse("WebFetch(domain:github.com)", "test"))
        Check("permissions: domain rule", ev(New WebFetchTool(), J(New With {.url = "https://api.github.com/x"})) = Verdict.Allow AndAlso ev(New WebFetchTool(), J(New With {.url = "https://example.com"})) = Verdict.Ask)
        Check("permissions: suggested rule for a command", p.SuggestRule("Bash", Jx.Parse(J(New With {.command = "git commit -m x"}))) = "Bash(git commit:*)")
        state.Mode = PermissionMode.BypassPermissions
        Check("permissions: bypass still honours deny rules", ev(New ReadTool(), J(New With {.file_path = "a/.env"})) = Verdict.Deny AndAlso ev(bash, J(New With {.command = "anything"})) = Verdict.Allow)
        state.Mode = PermissionMode.Default
    End Sub

    Private Shared Async Function ShellTests(state As AgentState, dir As String) As Task
        Dim bash As New BashTool(state.Shells)
        ' The same checks in the shell this platform uses (PowerShell on Windows, bash elsewhere).
        Dim posix = state.Shells.Kind = "bash"
        Dim say = Function(t As String) If(posix, "echo '" & t & "'", "Write-Output '" & t & "'")
        Dim r = Await bash.RunAsync(Jx.Parse(J(New With {.command = say("hello from shell")})), New ToolContext With {.State = state, .Cancel = CancellationToken.None})
        Check("bash: runs a command (" & state.Shells.Kind & ")", r.Ok AndAlso r.Content.Contains("hello from shell"), r.Content)
        Directory.CreateDirectory(Path.Combine(dir, "sub"))
        Await bash.RunAsync(Jx.Parse(J(New With {.command = "cd sub"})), New ToolContext With {.State = state, .Cancel = CancellationToken.None})
        Check("bash: cd carries over", state.Cwd.EndsWith("sub"), state.Cwd)
        state.Cwd = dir
        Dim failing = Await bash.RunAsync(Jx.Parse(J(New With {.command = If(posix, "sh -c 'exit 3'", "cmd /c exit 3")})), New ToolContext With {.State = state, .Cancel = CancellationToken.None})
        Check("bash: exit code is reported", Not failing.Ok AndAlso failing.Content.Contains("Exit code 3"), failing.Content)
        ' A command still running after its wait is not stopped: it goes on in the background, is audited, then stopped.
        Dim slow = Await bash.RunAsync(Jx.Parse(J(New With {.command = If(posix, "echo slow-start; sleep 60", "Write-Output slow-start; Start-Sleep -Seconds 60"), .timeout = 2000})), New ToolContext With {.State = state, .Cancel = CancellationToken.None})
        Dim slowId = Text.RegularExpressions.Regex.Match(slow.Content, "bash_\d+").Value
        Check("bash: a long command goes on in the background", slow.Ok AndAlso slow.Content.Contains("Still running after 2 s") AndAlso slowId.Length > 0, slow.Content)
        ' A busy machine may start the shell slowly: its first line comes with the hand-over or, waited for, with the audit.
        Dim audit = Await New BashOutputTool(state.Shells).RunAsync(Jx.Parse(J(New With {.bash_id = slowId, .wait_seconds = If(slow.Content.Contains("slow-start"), 0, 30)})), New ToolContext With {.State = state, .Cancel = CancellationToken.None})
        Check("bash: BashOutput audits it (running for, last output)", audit.Content.Contains("<status>running for") AndAlso audit.Content.Contains("last output") AndAlso (slow.Content & audit.Content).Contains("slow-start"), audit.Content)
        Dim killed = Await New KillShellTool(state.Shells).RunAsync(Jx.Parse(J(New With {.shell_id = slowId})), New ToolContext With {.State = state, .Cancel = CancellationToken.None})
        Check("bash: KillShell stops it", killed.Ok, killed.Content)
        If Not posix Then
            ' Something the command starts keeps its output open: the call still returns when the shell ends, and KillShell stops it.
            For Each p In Diagnostics.Process.GetProcessesByName("PING")
                p.Kill()
            Next
            Dim watch = Diagnostics.Stopwatch.StartNew()
            Dim holder = Await bash.RunAsync(Jx.Parse(J(New With {.command = "cmd /c 'start /b ping -n 120 127.0.0.1'", .timeout = 60000})), New ToolContext With {.State = state, .Cancel = CancellationToken.None})
            Dim holderId = Text.RegularExpressions.Regex.Match(holder.Content, "bash_\d+").Value
            Check("bash: returns when the shell ends, even if something it started holds the output", watch.Elapsed.TotalSeconds < 20 AndAlso holderId.Length > 0 AndAlso holder.Content.Contains("still running and printing"), $"{watch.Elapsed.TotalSeconds:0.0}s {holder.Content}")
            Await New KillShellTool(state.Shells).RunAsync(Jx.Parse(J(New With {.shell_id = holderId})), New ToolContext With {.State = state, .Cancel = CancellationToken.None})
            Await Task.Delay(1500)
            Check("bash: KillShell stops what outlived the shell", Diagnostics.Process.GetProcessesByName("PING").Length = 0, $"{Diagnostics.Process.GetProcessesByName("PING").Length} ping(s) left")
        End If
        Dim bg = Await bash.RunAsync(Jx.Parse(J(New With {.command = If(posix, "echo started; sleep 0.3; echo finished", "Write-Output started; Start-Sleep -Milliseconds 300; Write-Output finished"), .run_in_background = True})), New ToolContext With {.State = state, .Cancel = CancellationToken.None})
        Dim id = Text.RegularExpressions.Regex.Match(bg.Content, "bash_\d+").Value
        Dim outR = Await New BashOutputTool(state.Shells).RunAsync(Jx.Parse(J(New With {.bash_id = id, .wait_seconds = 30})), New ToolContext With {.State = state, .Cancel = CancellationToken.None})
        Check("bash: background command and BashOutput (waiting for it)", outR.Content.Contains("finished") AndAlso outR.Content.Contains("exited") AndAlso Not outR.Content.Contains("__AGENTIC_CWD__"), outR.Content)
    End Function

    Private Shared Sub SettingsTests(dir As String)
        Dim s = Settings.Load(dir)
        s.AddToList("local", "permissions.allow", "Bash(ls:*)")
        s.SetValue("project", "model", Text.Json.Nodes.JsonValue.Create("m1"))
        s.SetValue("local", "model", Text.Json.Nodes.JsonValue.Create("m2"))
        Dim again = Settings.Load(dir)
        Check("settings: later scopes win and lists merge", again.Str("model") = "m2" AndAlso again.Strings("permissions.allow").Contains("Bash(ls:*)"))
        Check("settings: local file is git-ignored", File.ReadAllText(Path.Combine(dir, ".agentic", ".gitignore")).Contains("settings.local.json"))
        File.WriteAllText(Path.Combine(dir, ".agentic", "settings.json"), "{""hooks"":{""PreToolUse"":[{""matcher"":""Bash"",""hooks"":[{""type"":""command"",""command"":""echo blocked 1>&2 & exit 2""}]}]}}")
        Dim st As New AgentState With {.ProjectDir = dir, .Cwd = dir, .Settings = Settings.Load(dir)}
        Dim h = New HookRunner(st).Run("PreToolUse", "Bash", New Dictionary(Of String, Object) From {{"tool_name", "Bash"}})
        Check("hooks: exit code 2 blocks with stderr", h.Blocked AndAlso h.Reason.Contains("blocked"), If(h.Reason, "(none)"))
        Dim none = New HookRunner(st).Run("PreToolUse", "Read", New Dictionary(Of String, Object))
        Check("hooks: matcher limits the hook", Not none.Blocked AndAlso none.Ran = 0)
    End Sub

    Private Shared Sub MiscTests(dir As String)
        File.WriteAllText(Path.Combine(dir, "AGENTS.md"), "Use tabs." & vbLf & "@extra.md")
        File.WriteAllText(Path.Combine(dir, "extra.md"), "Run tests with make test.")
        Dim mem = Memory.Load(dir, False)
        Check("memory: AGENTS.md with an @import", mem.Any(Function(m) m.Content.Contains("Use tabs.") AndAlso m.Content.Contains("make test")))
        Directory.CreateDirectory(Path.Combine(dir, ".agentic", "commands"))
        File.WriteAllText(Path.Combine(dir, ".agentic", "commands", "fix.md"), "---" & vbLf & "description: Fix an issue" & vbLf & "---" & vbLf & "Fix issue #$ARGUMENTS now.")
        Dim store As New Skills.SkillStore(dir, False)
        Dim cmdInfo = store.FindByName("/fix")
        Check("commands: custom command with $ARGUMENTS", cmdInfo IsNot Nothing AndAlso Skills.SkillStore.Expand(cmdInfo, "123") = "Fix issue #123 now.")
        Dim o = CliOptions.Parse({"-p", "do it", "--allowedTools", "Bash(git log:*)", "Edit", "--model", "m", "--max-turns", "5"})
        Check("cli: options", o.Print AndAlso o.Prompt = "do it" AndAlso o.AllowedTools.Count = 2 AndAlso o.Model = "m" AndAlso o.MaxTurns = 5, String.Join("|", o.AllowedTools))
        Dim o2 = CliOptions.Parse({"mcp", "add", "x", "--", "npx", "y"})
        Check("cli: subcommand", o2.Command = "mcp" AndAlso o2.CommandArgs.Count = 5)
        Dim o4 = CliOptions.Parse({"update", "--check"})
        Check("update: command and --check", o4.Command = "update" AndAlso o4.CommandArgs.Contains("--check") AndAlso o4.Errors.Count = 0)
        ' A build with agentic.dll beside it (bin\Debug) must never try to replace itself.
        Check("update: development builds never update", Updater.IsUpdatableBuild() OrElse Updater.DisabledReason(Nothing) = "development build" OrElse
                                                         Updater.DisabledReason(Nothing).StartsWith("turned off", StringComparison.Ordinal))
        Dim o3 = CliOptions.Parse({"--input-format", "stream-json", "--permission-prompt", "deny"})
        Check("host: stream-json options", o3.InputFormat = "stream-json" AndAlso o3.PermissionPrompt = "deny")
        Dim texts = {"{""type"":""user"",""message"":""a""}", "{""type"":""user"",""message"":{""content"":""b""}}",
                     "{""type"":""user"",""message"":{""role"":""user"",""content"":[{""type"":""text"",""text"":""c""},{""type"":""image""},{""type"":""text"",""text"":""d""}]}}",
                     "{""type"":""user"",""content"":""e""}"}.Select(Function(s) PrintMode.MessageText(Jx.Parse(s))).ToList()
        Check("host: user message shapes", String.Join("|", texts) = "a|b|c" & vbLf & "d|e", String.Join("|", texts))
        Dim html = WebFetchTool.HtmlToText("<html><head><title>T</title><script>x()</script></head><body><h2>Head</h2><p>Hello <a href=""/x"">link</a></p><ul><li>one</li></ul></body></html>", New Uri("https://example.com/a"))
        Check("webfetch: HTML to text", html.Contains("# T") AndAlso html.Contains("## Head") AndAlso html.Contains("[link](https://example.com/x)") AndAlso html.Contains("- one") AndAlso Not html.Contains("x()"), html)
        Dim sub2 = Subagents.All(dir, False)
        Check("subagents: built-in types", sub2.Any(Function(a) a.Name = "Explore") AndAlso sub2.Any(Function(a) a.Name = "general-purpose"))
    End Sub

    ''' <summary>Claude Code features mirrored later: its flags, PermissionRequest / updatedInput hooks, --disable-slash-commands, --fallback-model.</summary>
    Private Shared Sub ClaudeParityTests(dir As String)
        Dim sysFile = Path.Combine(dir, "sys.txt")
        File.WriteAllText(sysFile, "You are a tester.")
        Dim moreFile = Path.Combine(dir, "more.txt")
        File.WriteAllText(moreFile, "Be brief.")
        Dim o = CliOptions.Parse({"-p", "--system-prompt-file", sysFile, "--append-system-prompt", "One.", "--append-system-prompt-file", moreFile, "-n", "My run",
                                  "--tools", "Read", "Grep", "--strict-mcp-config", "--fork-session", "-c", "--no-session-persistence", "--replay-user-messages",
                                  "--disable-slash-commands", "--fallback-model", "fast/a", "--max-budget-usd", "2.5", "hello"})
        Check("cli: Claude Code flags", o.SystemPrompt = "You are a tester." AndAlso o.AppendSystemPrompt.Contains("One.") AndAlso o.AppendSystemPrompt.Contains("Be brief.") AndAlso
                                        o.Name = "My run" AndAlso o.Tools.SequenceEqual({"Read", "Grep"}) AndAlso o.StrictMcpConfig AndAlso o.ForkSession AndAlso o.ContinueLast AndAlso
                                        o.NoSessionPersistence AndAlso o.ReplayUserMessages AndAlso o.DisableSlashCommands AndAlso o.FallbackModel = "fast/a" AndAlso
                                        o.MaxBudgetUsd.GetValueOrDefault() = 2.5D AndAlso o.Prompt = "hello" AndAlso o.Errors.Count = 0, String.Join("; ", o.Errors))
        Dim noTools = CliOptions.Parse({"--tools", "", "-p", "x"})
        Check("cli: --tools """" leaves no built-in tools", noTools.Tools IsNot Nothing AndAlso noTools.Tools.Count = 0 AndAlso noTools.Prompt = "x")
        Dim auth = CliOptions.Parse({"auth", "status"})
        Check("cli: auth subcommand", auth.Command = "auth" AndAlso auth.CommandArgs.FirstOrDefault() = "status")
        Check("cli: a missing prompt file is an error", CliOptions.Parse({"--system-prompt-file", Path.Combine(dir, "nope.txt")}).Errors.Count = 1)

        ' Hooks that answer with JSON: PermissionRequest decides, PreToolUse rewrites the tool's input.
        Dim hooksDir = Path.Combine(dir, "hooks-json")
        Directory.CreateDirectory(Path.Combine(hooksDir, ".agentic"))
        Dim echoJson = Function(json As String) If(OperatingSystem.IsWindows(), "echo " & json, "echo '" & json & "'")
        Dim hook = Function(matcher As String, json As String) New Text.Json.Nodes.JsonArray(New Text.Json.Nodes.JsonObject From {
                       {"matcher", matcher}, {"hooks", New Text.Json.Nodes.JsonArray(New Text.Json.Nodes.JsonObject From {{"type", "command"}, {"command", echoJson(json)}})}})
        Dim root As New Text.Json.Nodes.JsonObject From {{"hooks", New Text.Json.Nodes.JsonObject From {
            {"PermissionRequest", hook("Bash", "{""hookSpecificOutput"":{""hookEventName"":""PermissionRequest"",""decision"":{""behavior"":""deny"",""message"":""not here""}}}")},
            {"PreToolUse", hook("Write", "{""hookSpecificOutput"":{""hookEventName"":""PreToolUse"",""updatedInput"":{""file_path"":""safe.txt""}}}")}}}}
        File.WriteAllText(Path.Combine(hooksDir, ".agentic", "settings.json"), root.ToJsonString())
        Dim hs As New AgentState With {.ProjectDir = hooksDir, .Cwd = hooksDir, .Settings = Settings.Load(hooksDir)}
        Dim pr = New HookRunner(hs).Run("PermissionRequest", "Bash", New Dictionary(Of String, Object) From {{"tool_name", "Bash"}})
        Check("hooks: PermissionRequest decides (behavior deny)", pr.PermissionDecision = "deny" AndAlso pr.Reason = "not here", $"{pr.PermissionDecision} / {pr.Reason}")
        Dim pre = New HookRunner(hs).Run("PreToolUse", "Write", New Dictionary(Of String, Object) From {{"tool_name", "Write"}})
        Check("hooks: PreToolUse updatedInput rewrites the input", pre.UpdatedInput.HasValue AndAlso Jx.StrOf(pre.UpdatedInput.Value, "file_path") = "safe.txt")

        Dim off As New Skills.SkillStore(dir, False) With {.Disabled = True}
        Check("skills: --disable-slash-commands lists none", off.All().Count = 0 AndAlso off.FindByName("/fix") Is Nothing)
        Check("fallback: overloaded or unavailable models fall back", Session.IsUnavailableError("FLINT: 529 overloaded_error") AndAlso
                                                                     Session.IsUnavailableError("No available channel for model x") AndAlso
                                                                     Not Session.IsUnavailableError("the request exceeds the available context size"))
    End Sub

End Class
