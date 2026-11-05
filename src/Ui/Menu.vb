Option Strict On
Option Explicit On
Option Infer On

Imports System.Text

Namespace Ui

    Friend NotInheritable Class MenuOption
        Public Property Label As String
        Public Property Hint As String
        ''' <summary>Shift+Tab picks this option (e.g. "allow all edits").</summary>
        Public Property ShiftTab As Boolean
        Public Property Value As String

        Public Sub New(label As String, Optional hint As String = Nothing, Optional value As String = Nothing, Optional shiftTab As Boolean = False)
            Me.Label = label
            Me.Hint = hint
            Me.Value = If(value, label)
            Me.ShiftTab = shiftTab
        End Sub
    End Class

    ''' <summary>
    ''' Arrow-key menus (❯ 1. Yes) and short text prompts. Keys: ↑/↓ or the number, Enter to choose, Esc to cancel.
    ''' Without a terminal (piped input) the options are listed and a number is read.
    ''' </summary>
    Friend NotInheritable Class Menu

        Public Shared Term As ITerminal = New RealTerminal()
        ''' <summary>Held while a menu reads keys, so the key watcher of a running answer does not take them.</summary>
        Public Shared ReadOnly InputLock As New Threading.SemaphoreSlim(1, 1)
        ''' <summary>
        ''' Checked while a menu or text prompt waits for a key: True closes it as if Esc was pressed (and sets Aborted) - the
        ''' question or approval was answered in another window. Set by the caller around one prompt.
        ''' </summary>
        Public Shared Property Abort As Func(Of Boolean)
        ''' <summary>The last menu or prompt was closed by Abort.</summary>
        Public Shared Property Aborted As Boolean

        ''' <summary>The footer: how to move, choose and cancel with the keys set in /keys.</summary>
        Private Shared Function MoveHint(canFilter As Boolean) As String
            Dim key = Function(id As String) KeyBindings.ShortLabel(id)
            Return "   " & If(canFilter, "type to filter · ", "") & key("menu-up") & key("menu-down") &
                   If(canFilter, " " & key("menu-page-up") & " " & key("menu-page-down"), "") & " to move · " & key("menu-pick") & " to choose · " &
                   key("menu-cancel") & If(canFilter, " to clear / cancel", " to cancel")
        End Function

        ''' <summary>Waits for a key while Abort stays False; False when aborted.</summary>
        Private Shared Function WaitForKey() As Boolean
            Dim check = Abort
            If check Is Nothing Then Return True
            While Not Term.KeyAvailable
                If check.Invoke() Then
                    Aborted = True
                    Return False
                End If
                Threading.Thread.Sleep(40)
            End While
            Return True
        End Function

        ''' <summary>
        ''' Returns the chosen index (in options), or -1 for Esc. Long lists scroll in a window that fits the terminal and can be
        ''' filtered by typing (on by default for more than 9 options); short ones also take the option's number.
        ''' </summary>
        Public Shared Function [Select](title As String, options As IList(Of MenuOption), Optional selected As Integer = 0, Optional footer As String = Nothing,
                                        Optional filterable As Boolean? = Nothing, Optional initialQuery As String = "") As Integer
            If options Is Nothing OrElse options.Count = 0 Then Return -1
            If Not Ansi.Interactive Then Return SelectPlain(title, options)
            Dim canFilter = If(filterable, options.Count > 9)
            Aborted = False
            InputLock.Wait()
            Try
                Out.StopSpinner()
                Out.EnsureNewLine()
                Dim query = If(canFilter, If(initialQuery, ""), "")
                Dim matches = Filter(options, query)
                Dim start = Math.Max(0, Math.Min(options.Count - 1, selected))
                Dim cursor = Math.Max(0, matches.IndexOf(start))
                Dim top = 0
                Dim rows = 0
                Dim refilter = Sub()
                                   Dim current = If(cursor >= 0 AndAlso cursor < matches.Count, matches(cursor), -1)
                                   matches = Filter(options, query)
                                   cursor = Math.Max(0, If(query.Length = 0, matches.IndexOf(current), 0))
                                   top = 0
                               End Sub
                Term.Write(Ansi.HideCursor)
                Try
                    While True
                        Dim compact = canFilter OrElse WrappedLines(title, options) > Term.Lines - 3
                        Dim page = If(compact, PageSize(title, canFilter), options.Count)
                        If cursor < top Then top = cursor
                        If cursor >= top + page Then top = cursor - page + 1
                        top = Math.Max(0, Math.Min(top, Math.Max(0, matches.Count - page)))
                        Dim text = If(compact, RenderCompact(title, options, matches, cursor, top, page, canFilter, query, footer), RenderFull(title, options, cursor, footer))
                        Term.Write(If(rows > 0, ChrW(13) & Ansi.Up(rows) & Ansi.ClearDown, "") & text)
                        rows = text.Count(Function(ch) ch = ChrW(10))
                        If Not WaitForKey() Then Return Finish(rows, title, options, -1)
                        Dim k = Term.ReadKey()
                        ' Keys from /keys (KeyBindings). In a list that filters, a plain character is typed into the filter.
                        Dim chord = KeyBindings.Chord(k)
                        Dim plain = k.KeyChar >= " "c AndAlso (k.Modifiers And (ConsoleModifiers.Control Or ConsoleModifiers.Alt)) = 0
                        Dim isKey = Function(id As String) Not (canFilter AndAlso plain) AndAlso KeyBindings.Matches(id, chord)
                        Dim shiftTab = options.ToList().FindIndex(Function(o) o.ShiftTab)
                        If shiftTab >= 0 AndAlso isKey("cycle-mode") Then
                            Return Finish(rows, title, options, shiftTab)
                        ElseIf isKey("menu-up") Then
                            If matches.Count > 0 Then cursor = (cursor - 1 + matches.Count) Mod matches.Count
                        ElseIf isKey("menu-down") Then
                            If matches.Count > 0 Then cursor = (cursor + 1) Mod matches.Count
                        ElseIf isKey("menu-page-up") Then
                            cursor = Math.Max(0, cursor - page)
                        ElseIf isKey("menu-page-down") Then
                            cursor = Math.Max(0, Math.Min(matches.Count - 1, cursor + page))
                        ElseIf isKey("menu-first") Then
                            cursor = 0
                        ElseIf isKey("menu-last") Then
                            cursor = Math.Max(0, matches.Count - 1)
                        ElseIf isKey("menu-pick") Then
                            If matches.Count > 0 Then Return Finish(rows, title, options, matches(cursor))
                        ElseIf isKey("menu-clear-filter") Then
                            query = ""
                            refilter()
                        ElseIf isKey("menu-cancel") Then
                            If query.Length > 0 Then
                                query = ""
                                refilter()
                            Else
                                Return Finish(rows, title, options, -1)
                            End If
                        ElseIf k.Key = ConsoleKey.Backspace Then
                            If query.Length > 0 Then
                                query = query.Substring(0, query.Length - 1)
                                refilter()
                            End If
                        ElseIf canFilter Then
                            If plain Then
                                query &= k.KeyChar
                                refilter()
                            End If
                        Else
                            Dim n As Integer
                            If Integer.TryParse(k.KeyChar.ToString(), n) AndAlso n >= 1 AndAlso n <= options.Count Then Return Finish(rows, title, options, n - 1)
                        End If
                    End While
                Finally
                    Term.Write(Ansi.ShowCursor)
                End Try
            Finally
                InputLock.Release()
            End Try
            Return -1
        End Function

        ''' <summary>The options that match a query (every word must appear in the label, hint or value), best first.</summary>
        Friend Shared Function Filter(options As IList(Of MenuOption), query As String) As List(Of Integer)
            Dim all = Enumerable.Range(0, options.Count).ToList()
            Dim q = If(query, "").Trim().ToLowerInvariant()
            If q.Length = 0 Then Return all
            Dim words = q.Split(" "c, StringSplitOptions.RemoveEmptyEntries)
            Dim scored As New List(Of (Index As Integer, Score As Integer))
            For Each i In all
                Dim o = options(i)
                Dim label = Ansi.Strip(If(o.Label, "")).ToLowerInvariant()
                Dim hay = label & " " & Ansi.Strip(If(o.Hint, "")).ToLowerInvariant() & " " & If(o.Value, "").ToLowerInvariant()
                If Not words.All(Function(w) hay.Contains(w)) Then Continue For
                Dim score = If(label.StartsWith(q), 0, If(label.Contains(q), 1, If(label.Split({"/"c, " "c, "-"c, ":"c}).Any(Function(part) part.StartsWith(words(0))), 2, 3)))
                scored.Add((i, score))
            Next
            Return scored.OrderBy(Function(s) s.Score).ThenBy(Function(s) s.Index).Select(Function(s) s.Index).ToList()
        End Function

        Private Shared Function TitleLines(title As String, w As Integer) As List(Of String)
            Dim list As New List(Of String)
            If String.IsNullOrEmpty(title) Then Return list
            For Each l In title.Split(ChrW(10))
                list.AddRange(Ansi.WrapText(l, w, " ", " "))
            Next
            Return list
        End Function

        ''' <summary>How many lines the full (wrapped, with hints) layout needs.</summary>
        Private Shared Function WrappedLines(title As String, options As IList(Of MenuOption)) As Integer
            Dim w = Term.Columns - 2
            Dim n = TitleLines(title, w).Count + 1
            For Each o In options
                n += Ansi.WrapText(o.Label & "    ", w, "    ", "     ").Count
                If Not String.IsNullOrEmpty(o.Hint) Then n += Ansi.WrapText(o.Hint, w, "      ", "      ").Count
                If n > 500 Then Exit For
            Next
            Return n
        End Function

        ''' <summary>Options shown at once in the compact layout: what fits under the title, at most 12.</summary>
        Private Shared Function PageSize(title As String, canFilter As Boolean) As Integer
            Dim w = Term.Columns - 2
            Dim used = TitleLines(title, w).Count + If(canFilter, 1, 0) + 3 ' two scroll lines and the footer
            Return Math.Max(3, Math.Min(12, Term.Lines - used - 2))
        End Function

        ''' <summary>The layout for short lists: wrapped labels with their hints below.</summary>
        Private Shared Function RenderFull(title As String, options As IList(Of MenuOption), index As Integer, footer As String) As String
            Dim sb As New StringBuilder()
            Dim w = Term.Columns - 2
            For Each l In TitleLines(title, w)
                sb.Append(l).Append(Environment.NewLine)
            Next
            For i = 0 To options.Count - 1
                Dim o = options(i)
                Dim mark = If(i = index, Ansi.Accent(Ansi.Pointer), " ")
                Dim label = $"{i + 1}. {o.Label}"
                Dim styled = If(i = index, Ansi.Accent(label), label)
                For Each l In Ansi.WrapText(styled, w, " " & mark & " ", "     ")
                    sb.Append(l).Append(Environment.NewLine)
                Next
                If Not String.IsNullOrEmpty(o.Hint) Then
                    For Each l In Ansi.WrapText(Ansi.Muted(o.Hint), w, "      ", "      ")
                        sb.Append(l).Append(Environment.NewLine)
                    Next
                End If
            Next
            sb.Append(Ansi.Muted(If(footer, MoveHint(False)))).Append(Environment.NewLine)
            Return sb.ToString()
        End Function

        ''' <summary>The layout for long lists: one line per option, a scrolling window, and the filter line.</summary>
        Private Shared Function RenderCompact(title As String, options As IList(Of MenuOption), matches As List(Of Integer), cursor As Integer, top As Integer, page As Integer,
                                              canFilter As Boolean, query As String, footer As String) As String
            Dim sb As New StringBuilder()
            Dim w = Term.Columns - 2
            For Each l In TitleLines(title, w)
                sb.Append(Ansi.Truncate(l, w)).Append(Environment.NewLine)
            Next
            If canFilter Then
                Dim count = If(query.Length = 0, $"{options.Count} options", $"{matches.Count} of {options.Count}")
                Dim box = If(query.Length = 0, Ansi.Dim2("type to filter"), Ansi.Text(query) & Ansi.Accent("▏"))
                sb.Append(Ansi.Truncate(" " & Ansi.Accent("⌕") & " " & box & "  " & Ansi.Muted(count), w)).Append(Environment.NewLine)
            End If
            Dim visible = matches.Skip(top).Take(page).ToList()
            sb.Append(If(top > 0, Ansi.Muted($"   ↑ {top} more"), "")).Append(Environment.NewLine)
            If visible.Count = 0 Then sb.Append(Ansi.Muted("   no match")).Append(Environment.NewLine)
            Dim numbers = Not canFilter
            Dim labelW = Math.Min(Math.Max(10, w \ 2), visible.Select(Function(i) Ansi.Width(options(i).Label)).DefaultIfEmpty(10).Max() + If(numbers, 4, 0))
            For n = 0 To visible.Count - 1
                Dim i = visible(n)
                Dim o = options(i)
                Dim isCur = top + n = cursor
                Dim label = If(numbers, $"{i + 1}. ", "") & o.Label
                Dim cell = Ansi.Pad(Ansi.Truncate(label, labelW), labelW)
                Dim line = " " & If(isCur, Ansi.Accent(Ansi.Pointer), " ") & " " & If(isCur, Ansi.Accent(Ansi.Strip(cell)), cell)
                If Not String.IsNullOrEmpty(o.Hint) Then line &= "  " & If(isCur, Ansi.Text(o.Hint), Ansi.Muted(o.Hint))
                sb.Append(Ansi.Truncate(line, w)).Append(Environment.NewLine)
            Next
            Dim below = matches.Count - top - visible.Count
            sb.Append(If(below > 0, Ansi.Muted($"   ↓ {below} more"), "")).Append(Environment.NewLine)
            sb.Append(Ansi.Muted(If(footer, MoveHint(canFilter)))).Append(Environment.NewLine)
            Return sb.ToString()
        End Function

        Private Shared Function Finish(rows As Integer, title As String, options As IList(Of MenuOption), index As Integer) As Integer
            ' Collapse the menu to one line that records the answer.
            Term.Write(ChrW(13) & Ansi.Up(rows) & Ansi.ClearDown)
            Dim answer = If(index < 0, Ansi.Muted("(cancelled)"), Ansi.Accent(Ansi.Strip(options(index).Label)))
            If Not String.IsNullOrEmpty(title) Then Term.Write(Ansi.Muted("  " & Ansi.Strip(FirstLine(title))) & " " & answer & Environment.NewLine)
            Return index
        End Function

        Private Shared Function FirstLine(s As String) As String
            Dim i = s.IndexOf(ChrW(10))
            Return If(i < 0, s, s.Substring(0, i))
        End Function

        Private Shared Function SelectPlain(title As String, options As IList(Of MenuOption)) As Integer
            Out.EnsureNewLine()
            If Not String.IsNullOrEmpty(title) Then Out.Line(title)
            For i = 0 To options.Count - 1
                Out.Line($"  {i + 1}. {options(i).Label}" & If(String.IsNullOrEmpty(options(i).Hint), "", " — " & options(i).Hint))
            Next
            Out.Write("Choose 1-" & options.Count & ": ")
            Dim line = Console.ReadLine()
            Dim n As Integer
            If line IsNot Nothing AndAlso Integer.TryParse(line.Trim(), n) AndAlso n >= 1 AndAlso n <= options.Count Then Return n - 1
            Return -1
        End Function

        ''' <summary>Asks for a line of text; Nothing when cancelled with Esc.</summary>
        Public Shared Function Ask(label As String, Optional initial As String = "", Optional secret As Boolean = False) As String
            If Not Ansi.Interactive Then
                Out.Write(label & " ")
                Return Console.ReadLine()
            End If
            Aborted = False
            InputLock.Wait()
            Try
                Out.StopSpinner()
                Out.EnsureNewLine()
                Dim ed As New LineEditor(Term) With {.Prompt = label & " ", .Boxed = False, .Secret = secret, .AllowMultiline = False, .Interrupt = Abort}
                Dim text = ed.ReadLine(initial)
                If ed.Interrupted Then Aborted = True
                Return text
            Finally
                InputLock.Release()
            End Try
        End Function

        Public Shared Function Confirm(question As String, Optional defaultYes As Boolean = True) As Boolean
            Dim i = [Select](question, {New MenuOption("Yes"), New MenuOption("No")}, If(defaultYes, 0, 1))
            Return i = 0
        End Function

    End Class

End Namespace
