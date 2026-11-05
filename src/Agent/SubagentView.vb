Option Strict On
Option Explicit On
Option Infer On

Imports System.Collections.Concurrent
Imports System.Text
Imports AgenticCli.Tools
Imports AgenticCli.Ui

Namespace Agent

    ''' <summary>
    ''' Subagents' streams in the terminal. While they work, a panel above the spinner shows each one's latest words (or what it
    ''' is thinking). What a subagent writes is printed under its Task line when it finishes a step - before that step's tool
    ''' calls - tagged with its name; Ctrl+O (verbose) prints it whole, otherwise a few lines per step.
    ''' </summary>
    Friend NotInheritable Class SubagentView
        Private Const StepLines As Integer = 6
        Private Const PanelChars As Integer = 240
        Private Const PanelAgents As Integer = 6

        Private NotInheritable Class Entry
            Public Label As String
            ''' <summary>This step's writing, not printed yet.</summary>
            Public ReadOnly Text As New StringBuilder()
            ''' <summary>The latest words (writing or thinking) for the panel.</summary>
            Public Tail As String = ""
            Public Thinking As Boolean
            Public ReadOnly Started As DateTime = DateTime.UtcNow
        End Class

        Private ReadOnly _entries As New ConcurrentDictionary(Of String, Entry)(StringComparer.Ordinal)
        Private ReadOnly _state As AgentState

        Public Sub New(state As AgentState)
            _state = state
        End Sub

        ''' <summary>A subagent starts (its key: its conversation, or its card's call id).</summary>
        Public Sub Start(key As String, label As String)
            _entries.GetOrAdd(key, Function(k) New Entry With {.Label = ShortLabel(label)})
            RefreshPanel()
        End Sub

        ''' <summary>Words it writes (or thinks): the panel follows them; writing is printed at the end of the step.</summary>
        Public Sub Write(key As String, label As String, text As String, thinking As Boolean)
            If String.IsNullOrEmpty(text) Then Return
            Dim e = _entries.GetOrAdd(key, Function(k) New Entry With {.Label = ShortLabel(label)})
            SyncLock e
                If Not thinking Then e.Text.Append(text)
                If e.Thinking <> thinking Then e.Tail = ""
                e.Thinking = thinking
                Dim t = e.Tail & text
                e.Tail = If(t.Length > PanelChars * 2, t.Substring(t.Length - PanelChars * 2), t)
            End SyncLock
            RefreshPanel()
        End Sub

        ''' <summary>The step is over (it calls a tool, or answers): what it wrote goes into the scrollback now.</summary>
        Public Sub Flush(key As String)
            Dim e As Entry = Nothing
            If Not _entries.TryGetValue(key, e) Then Return
            Dim text As String
            SyncLock e
                text = e.Text.ToString().Trim()
                e.Text.Clear()
                e.Tail = ""
            End SyncLock
            If text.Length > 0 Then Print(e.Label, text)
            RefreshPanel()
        End Sub

        ''' <summary>A tool call of a subagent the server runs (its own steps are printed by the CLI's tools).</summary>
        Public Sub ToolLine(key As String, title As String, ok As Boolean?)
            Dim e As Entry = Nothing
            Dim label = If(_entries.TryGetValue(key, e), e.Label, "subagent")
            Dim mark = If(ok.HasValue AndAlso Not ok.Value, Ansi.Bad(Ansi.Elbow & "  "), Ansi.Muted(Ansi.Elbow & "  "))
            Out.Line("    " & mark & Ansi.Accent("[" & label & "] ") & Ansi.Muted(Ansi.Truncate(title, Math.Max(20, Ansi.TermWidth() - 12 - Ansi.Width(label)))))
        End Sub

        ''' <summary>The subagent is done: the rest of what it wrote (its report) is printed and it leaves the panel.</summary>
        Public Sub Finish(key As String)
            Flush(key)
            Dim gone As Entry = Nothing
            _entries.TryRemove(key, gone)
            RefreshPanel()
        End Sub

        Private Sub Print(label As String, text As String)
            Dim lines = text.Replace(vbCr, "").Split(ChrW(10)).Select(Function(l) l.TrimEnd()).Where(Function(l) l.Trim().Length > 0).ToList()
            If lines.Count = 0 Then Return
            Dim shown = If(_state.Verbose, lines, lines.Take(StepLines).ToList())
            Dim tag = Ansi.Accent("[" & label & "] ")
            Dim pad = New String(" "c, Ansi.Width(label) + 3)
            Dim width = Math.Max(30, Ansi.TermWidth() - 8 - Ansi.Width(label))
            Dim first = True
            For Each l In shown
                For Each part In Ansi.WrapText(l, width, "", "")
                    Out.Line("    " & Ansi.Muted("│ ") & If(first, tag, pad) & Ansi.Muted(part))
                    first = False
                Next
            Next
            If lines.Count > shown.Count Then
                Out.Line("    " & Ansi.Muted("│ ") & pad & Ansi.Dim2($"… {lines.Count - shown.Count} more line{If(lines.Count - shown.Count = 1, "", "s")} (Ctrl+O shows subagents' writing in full)"))
            End If
        End Sub

        ''' <summary>The panel: one line per working subagent with its latest words.</summary>
        Private Sub RefreshPanel()
            Dim list = _entries.Values.OrderBy(Function(e) e.Started).ToList()
            If list.Count = 0 Then
                Out.SetPanel(Nothing)
                Return
            End If
            Dim lines As New List(Of String)
            For Each e In list.Take(PanelAgents)
                Dim tail As String, thinking As Boolean
                SyncLock e
                    tail = e.Tail
                    thinking = e.Thinking
                End SyncLock
                Dim words = Text.RegularExpressions.Regex.Replace(tail, "\s+", " ").Trim()
                If words.Length > PanelChars Then words = "…" & words.Substring(words.Length - PanelChars)
                lines.Add("  " & Ansi.Accent(Ansi.Diamond & " " & e.Label) & " " &
                          If(words.Length = 0, Ansi.Dim2("working…"), Ansi.Muted(If(thinking, "thinking: ", "") & words)))
            Next
            If list.Count > PanelAgents Then lines.Add("  " & Ansi.Dim2($"+{list.Count - PanelAgents} more subagents"))
            Out.SetPanel(lines)
        End Sub

        Private Shared Function ShortLabel(label As String) As String
            Return If(String.IsNullOrWhiteSpace(label), "subagent", ToolKit.OneLine(label, 28))
        End Function

    End Class

End Namespace
