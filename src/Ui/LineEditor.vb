Option Strict On
Option Explicit On
Option Infer On

Imports System.Text
Imports System.Text.RegularExpressions

Namespace Ui

    Friend NotInheritable Class Suggestion
        ''' <summary>What replaces the word being completed.</summary>
        Public Property Text As String
        Public Property Label As String
        Public Property Hint As String
        ''' <summary>Enter on this suggestion also sends the message (a slash command without arguments).</summary>
        Public Property Submit As Boolean
    End Class

    ''' <summary>
    ''' The message box. Multi-line editing (\ + Enter, Ctrl+J, Shift+Enter or pasting), history (↑/↓, Ctrl+R search),
    ''' completion of /commands and @files (Tab or Enter), big pastes collapsed to [Pasted text #1 +40 lines], undo (Ctrl+Z),
    ''' Emacs keys (Ctrl+A/E/U/K/W), Shift+Tab for the permission mode, Esc Esc to go back, Ctrl+C twice or Ctrl+D to quit.
    ''' These are the default keys: every one can be changed with /keys (KeyBindings).
    ''' </summary>
    Friend NotInheritable Class LineEditor

        Private ReadOnly _term As ITerminal
        Private _buf As New StringBuilder()
        Private _pos As Integer
        Private _rows As Integer        ' frame rows above the cursor at the last render
        Private _rendered As Boolean
        Private _suggestions As New List(Of Suggestion)
        Private _suggestStart As Integer
        Private _suggestIndex As Integer
        Private _suggestHidden As Boolean
        Private _historyIndex As Integer = -1
        Private _draft As String = ""
        Private _hint As String
        ' The last key (as /keys names it) and when it was pressed: pairs like Esc Esc.
        Private _prevChord As String
        Private _prevChordAt As DateTime
        Private _lastCtrlC As DateTime
        Private ReadOnly _undo As New Stack(Of (String, Integer))
        Private _searching As Boolean
        Private _searchQuery As String = ""
        Private _searchFrom As Integer
        Private _pasteCount As Integer

        Public Property Prompt As String = "> "
        Public Property Boxed As Boolean = True
        Public Property Secret As Boolean
        Public Property AllowMultiline As Boolean = True
        Public Property Placeholder As String
        Public Property History As New List(Of String)
        Public Property Footer As Func(Of String)
        Public Property FooterRight As Func(Of String)
        Public Property Suggest As Func(Of String, Integer, (Items As List(Of Suggestion), Start As Integer))
        Public Property OnShiftTab As Action
        ''' <summary>Esc twice on an empty box (Claude Code: rewind). The box is cleared from the screen while it runs.</summary>
        Public Property OnDoubleEsc As Action
        ''' <summary>Extra shortcuts; return True when the key was handled.</summary>
        Public Property OnKey As Func(Of ConsoleKeyInfo, Boolean)
        ''' <summary>Pasted blocks shown as placeholders, expanded when the message is sent.</summary>
        Public ReadOnly Property Pastes As New Dictionary(Of String, String)
        Public Property ExitRequested As Boolean
        Public Property MaxPasteChars As Integer = 1200
        ''' <summary>
        ''' Asked about twice a second while the box is empty and no key is pressed: a prompt it returns is submitted as if typed
        ''' (loops and the heartbeat). Nothing = keep waiting.
        ''' </summary>
        Public Property IdleTick As Func(Of String)
        ''' <summary>The last ReadLine returned IdleTick's prompt rather than typed text.</summary>
        Public Property LastWasIdle As Boolean
        ''' <summary>
        ''' Asked about every 40 ms while waiting for a key: True ends ReadLine at once (it returns Nothing, Interrupted is set and
        ''' InterruptedText keeps what was typed) - another window sent a message in this chat.
        ''' </summary>
        Public Property Interrupt As Func(Of Boolean)
        ''' <summary>The external-editor key (Ctrl+G): gets the message, returns the edited text (Nothing: unchanged).</summary>
        Public Property ExternalEdit As Func(Of String, String)
        ''' <summary>The paste-image key (Alt+V): returns the placeholder to insert ("[Image #1]"), or Nothing when the clipboard has no image.</summary>
        Public Property PasteImage As Func(Of String)
        Public Property Interrupted As Boolean
        Public Property InterruptedText As String
        ''' <summary>Vim keys: Esc switches to normal mode (motions and edits), i / a / A / I / o back to typing.</summary>
        Public Property VimMode As Boolean
        Private _vimNormal As Boolean
        Private _vimPending As String
        Private _vimRegister As String = ""

        ''' <summary>"NORMAL" or "INSERT" in vim mode, Nothing otherwise.</summary>
        Public ReadOnly Property VimState As String
            Get
                If Not VimMode Then Return Nothing
                Return If(_vimNormal, "NORMAL", "INSERT")
            End Get
        End Property

        Public Sub New(term As ITerminal)
            _term = term
        End Sub

        Public ReadOnly Property Buffer As String
            Get
                Return _buf.ToString()
            End Get
        End Property

        ''' <summary>Edits until Enter. Returns the text (pastes expanded), or Nothing on Ctrl+D / Ctrl+C twice / Esc (plain prompts).</summary>
        Public Function ReadLine(Optional initial As String = "") As String
            _buf.Clear()
            _buf.Append(If(initial, ""))
            _pos = _buf.Length
            _rows = 0
            _rendered = False
            _historyIndex = -1
            ExitRequested = False
            LastWasIdle = False
            Interrupted = False
            InterruptedText = Nothing
            _vimNormal = False
            _vimPending = Nothing
            Render()
            While True
                If IdleTick IsNot Nothing OrElse Interrupt IsNot Nothing Then
                    Dim due = WaitForKeyOrIdle()
                    If Interrupted Then
                        InterruptedText = _buf.ToString()
                        ClearFrame()
                        Return Nothing
                    End If
                    If due IsNot Nothing Then
                        ClearFrame()
                        LastWasIdle = True
                        Return due
                    End If
                End If
                Dim k = _term.ReadKey()
                Dim batch As New StringBuilder()
                Dim batchStart = _pos
                Dim batchOnlyInserts = True
                Dim result = HandleKey(k, batch, batchOnlyInserts)
                ' Keys that arrive together (a paste) are applied before the next render.
                Dim guard = 0
                While result Is Nothing AndAlso _term.KeyAvailable AndAlso guard < 200000
                    guard += 1
                    result = HandleKey(_term.ReadKey(), batch, batchOnlyInserts)
                End While
                If result Is Nothing AndAlso batchOnlyInserts AndAlso batch.Length > 0 Then CollapsePaste(batchStart, batch.ToString())
                If result Is Nothing Then
                    Render()
                    Continue While
                End If
                If result = ChrW(0) Then
                    ClearFrame()
                    Return Nothing
                End If
                Return Finish()
            End While
            Return Nothing
        End Function

        ''' <summary>Waits for a key; meanwhile, while the box is empty, IdleTick may hand over a prompt (returned, else Nothing).</summary>
        Private Function WaitForKeyOrIdle() As String
            Dim nextCheck = DateTime.MinValue
            While Not _term.KeyAvailable
                If Interrupt IsNot Nothing AndAlso Interrupt.Invoke() Then
                    Interrupted = True
                    Return Nothing
                End If
                If IdleTick IsNot Nothing AndAlso _buf.Length = 0 AndAlso Not _searching AndAlso DateTime.UtcNow >= nextCheck Then
                    nextCheck = DateTime.UtcNow.AddMilliseconds(500)
                    Dim due = IdleTick.Invoke()
                    If Not String.IsNullOrEmpty(due) Then Return due
                End If
                Threading.Thread.Sleep(40)
            End While
            Return Nothing
        End Function

        ''' <summary>Returns Nothing to keep editing, ChrW(0) to cancel, anything else to submit.</summary>
        Private Function HandleKey(k As ConsoleKeyInfo, batch As StringBuilder, ByRef onlyInserts As Boolean) As String
            Dim ctrl = (k.Modifiers And ConsoleModifiers.Control) <> 0
            Dim alt = (k.Modifiers And ConsoleModifiers.Alt) <> 0
            ' Keys come from /keys (KeyBindings): the key as it names it, and the one before it for pairs like Esc Esc.
            Dim chord = KeyBindings.Chord(k)
            Dim prev = _prevChord
            Dim prevAt = _prevChordAt
            _prevChord = chord
            _prevChordAt = DateTime.UtcNow
            Dim isKey = Function(id As String) KeyBindings.Matches(id, chord, prev, prevAt)
            If _searching Then Return SearchKey(k, isKey)
            ' Vim normal mode: letters are commands; Enter, arrows, Tab and Esc keep their usual meaning.
            If VimMode AndAlso _vimNormal AndAlso Not ctrl AndAlso Not alt AndAlso k.KeyChar >= " "c AndAlso
               k.Key <> ConsoleKey.Enter AndAlso k.Key <> ConsoleKey.Tab AndAlso k.Key <> ConsoleKey.Escape Then
                onlyInserts = False
                Return VimKey(k.KeyChar)
            End If
            ' The session's own keys (verbose, the todo list …) for every key that does not type a character.
            If OnKey IsNot Nothing AndAlso chord.Length > 0 AndAlso Not PlainCharacter(k) Then
                If OnKey.Invoke(k) Then Return Nothing
            End If
            Dim before = _buf.Length
            Dim plainEnter = k.Key = ConsoleKey.Enter AndAlso k.Modifiers = 0 AndAlso k.KeyChar <> ChrW(10)
            If plainEnter AndAlso _term.KeyAvailable AndAlso AllowMultiline Then
                ' Part of a paste: keep the line break.
                InsertText(vbLf, batch)
                Return Nothing
            End If
            ' A one-line box always sends on Enter.
            If isKey("submit") OrElse (Not AllowMultiline AndAlso k.Key = ConsoleKey.Enter) Then
                If SuggestionsVisible() Then
                    Dim s = _suggestions(_suggestIndex)
                    Dim tokenNow = _buf.ToString(_suggestStart, _pos - _suggestStart)
                    ' The send key takes the highlighted suggestion; for a command it also runs it.
                    If Not tokenNow.Equals(s.Text.TrimEnd(), StringComparison.OrdinalIgnoreCase) Then
                        AcceptSuggestion()
                        If Not s.Submit Then Return Nothing
                    End If
                End If
                If AllowMultiline AndAlso _pos > 0 AndAlso _pos = _buf.Length AndAlso _buf(_pos - 1) = "\"c Then
                    _buf.Remove(_pos - 1, 1)
                    _pos -= 1
                    InsertText(vbLf, Nothing)
                    Return Nothing
                End If
                Return _buf.ToString() & ChrW(1)
            End If
            ' Enter adds a line when sending is on another key.
            If isKey("newline") OrElse plainEnter Then
                If AllowMultiline Then InsertText(vbLf, batch)
                Return Nothing
            End If
            If isKey("cycle-mode") Then
                OnShiftTab?.Invoke()
                Return Nothing
            End If
            If isKey("accept-suggestion") Then
                If SuggestionsVisible() Then AcceptSuggestion()
                Return Nothing
            End If
            ' Esc: hides suggestions, Vim normal mode, cancels outside the box; twice: clears the box or goes back.
            If isKey("dismiss") Then
                If SuggestionsVisible() Then
                    _suggestHidden = True
                    _prevChord = Nothing
                    Return Nothing
                End If
                If VimMode AndAlso Not _vimNormal AndAlso Boxed Then
                    ' Insert to normal mode; the cursor steps back onto the last character, like Vim.
                    _vimNormal = True
                    _vimPending = Nothing
                    If _pos > LineStart(_pos) Then _pos = PrevIndex(_pos)
                    _prevChord = Nothing
                    Return Nothing
                End If
                If Not Boxed Then Return ChrW(0)
            End If
            If _buf.Length > 0 AndAlso isKey("clear") Then
                SaveUndo()
                _buf.Clear()
                _pos = 0
                _hint = Nothing
                _prevChord = Nothing
                Return Nothing
            End If
            If _buf.Length = 0 AndAlso OnDoubleEsc IsNot Nothing AndAlso isKey("rewind") Then
                ClearFrame()
                OnDoubleEsc.Invoke()
                _rows = 0
                _rendered = False
                _hint = Nothing
                _prevChord = Nothing
                Return Nothing
            End If
            ' The first key of such a pair: say what the second one does.
            Dim pairs = If(_buf.Length > 0, KeyBindings.PairStarts("clear"),
                           If(OnDoubleEsc IsNot Nothing, KeyBindings.PairStarts("rewind"), New List(Of (First As String, Second As String))))
            Dim started = pairs.FirstOrDefault(Function(p) p.First = chord)
            If started.First IsNot Nothing Then
                _hint = $"{started.Second} again to " & If(_buf.Length > 0, "clear", "go back to an earlier message")
                Return Nothing
            End If
            If isKey("dismiss") Then
                _hint = Nothing
                Return Nothing
            End If
            If isKey("interrupt") Then
                onlyInserts = False
                If _buf.Length > 0 Then
                    SaveUndo()
                    _buf.Clear()
                    _pos = 0
                    Return Nothing
                End If
                If Not Boxed Then Return ChrW(0)
                If (DateTime.UtcNow - _lastCtrlC).TotalMilliseconds < 1500 Then
                    ExitRequested = True
                    Return ChrW(0)
                End If
                _lastCtrlC = DateTime.UtcNow
                _hint = $"Press {KeyBindings.Label("interrupt")} again to exit"
                Return Nothing
            End If
            If ExternalEdit IsNot Nothing AndAlso isKey("external-editor") Then
                onlyInserts = False
                ClearFrame()
                Dim edited = ExternalEdit.Invoke(_buf.ToString())
                If edited IsNot Nothing Then
                    SaveUndo()
                    SetBuffer(edited.TrimEnd(ChrW(13), ChrW(10), " "c, ChrW(9)))
                End If
                _hint = If(edited Is Nothing, "The message is unchanged", Nothing)
                Return Nothing
            End If
            If PasteImage IsNot Nothing AndAlso isKey("paste-image") Then
                onlyInserts = False
                Dim token = PasteImage.Invoke()
                If token Is Nothing Then
                    _hint = "No image on the clipboard"
                Else
                    InsertText(token & " ", Nothing)
                    _hint = Nothing
                End If
                Return Nothing
            End If
            If isKey("exit") Then
                onlyInserts = False
                If _buf.Length = 0 Then
                    ExitRequested = True
                    Return ChrW(0)
                End If
                If _pos < _buf.Length Then _buf.Remove(_pos, 1)
                Return Nothing
            End If
            If Not EditKey(isKey) Then
                ' Not a key of any action: type the character (AltGr characters too); other keys do nothing.
                Dim ch = k.KeyChar
                If ch = ChrW(10) AndAlso AllowMultiline Then
                    InsertText(vbLf, batch)
                ElseIf ch = ChrW(9) AndAlso k.Key <> ConsoleKey.Tab Then
                    InsertText("  ", batch)
                ElseIf PlainCharacter(k) Then
                    InsertText(ch.ToString(), batch)
                Else
                    onlyInserts = False
                End If
            Else
                onlyInserts = False
            End If
            If _buf.Length <> before Then _suggestHidden = False
            _hint = Nothing
            Return Nothing
        End Function

        ''' <summary>A key that types a character: no Ctrl or Alt (AltGr - Ctrl+Alt - types one too).</summary>
        Private Shared Function PlainCharacter(k As ConsoleKeyInfo) As Boolean
            Dim ctrl = (k.Modifiers And ConsoleModifiers.Control) <> 0
            Dim alt = (k.Modifiers And ConsoleModifiers.Alt) <> 0
            Return (k.KeyChar >= " "c OrElse Char.IsSurrogate(k.KeyChar)) AndAlso (ctrl = alt)
        End Function

        ''' <summary>Moving and deleting in the box; False when the key is none of those.</summary>
        Private Function EditKey(isKey As Func(Of String, Boolean)) As Boolean
            If isKey("delete-word") Then
                DeleteWordBack()
            ElseIf isKey("backspace") Then
                Backspace()
            ElseIf isKey("delete") Then
                If _pos < _buf.Length Then
                    SaveUndo()
                    _buf.Remove(_pos, If(Char.IsHighSurrogate(_buf(_pos)) AndAlso _pos + 1 < _buf.Length, 2, 1))
                End If
            ElseIf isKey("word-left") Then
                _pos = WordLeft(_pos)
            ElseIf isKey("word-right") Then
                _pos = WordRight(_pos)
            ElseIf isKey("left") Then
                _pos = PrevIndex(_pos)
            ElseIf isKey("right") Then
                If _pos = _buf.Length AndAlso SuggestionsVisible() Then AcceptSuggestion() Else _pos = NextIndex(_pos)
            ElseIf isKey("start") Then
                _pos = 0
            ElseIf isKey("end") Then
                _pos = _buf.Length
            ElseIf isKey("line-start") Then
                _pos = LineStart(_pos)
            ElseIf isKey("line-end") Then
                _pos = LineEnd(_pos)
            ElseIf isKey("up") Then
                If SuggestionsVisible() Then
                    _suggestIndex = (_suggestIndex - 1 + _suggestions.Count) Mod _suggestions.Count
                ElseIf LineStart(_pos) > 0 Then
                    MoveVertical(-1)
                Else
                    HistoryMove(1)
                End If
            ElseIf isKey("down") Then
                If SuggestionsVisible() Then
                    _suggestIndex = (_suggestIndex + 1) Mod _suggestions.Count
                ElseIf LineEnd(_pos) < _buf.Length Then
                    MoveVertical(1)
                Else
                    HistoryMove(-1)
                End If
            ElseIf isKey("delete-to-start") Then
                SaveUndo()
                Dim s = LineStart(_pos)
                _buf.Remove(s, _pos - s)
                _pos = s
            ElseIf isKey("delete-to-end") Then
                SaveUndo()
                Dim e = LineEnd(_pos)
                _buf.Remove(_pos, e - _pos)
            ElseIf isKey("undo") Then
                If _undo.Count > 0 Then
                    Dim u = _undo.Pop()
                    _buf.Clear()
                    _buf.Append(u.Item1)
                    _pos = Math.Min(u.Item2, _buf.Length)
                End If
            ElseIf isKey("clear-screen") Then
                _term.Write(Ansi.E & "[2J" & Ansi.E & "[3J" & Ansi.E & "[H")
                _rows = 0
                _rendered = False
            ElseIf isKey("history-search") Then
                If History.Count > 0 Then
                    _searching = True
                    _searchQuery = ""
                    _searchFrom = History.Count - 1
                    _draft = _buf.ToString()
                End If
            Else
                Return False
            End If
            Return True
        End Function

        ' ------------------------------------------------------------------ vim normal mode

        Private Sub VimInsert()
            _vimNormal = False
            _vimPending = Nothing
        End Sub

        ''' <summary>The start of the next word (Vim's w): past this word, then past the spaces.</summary>
        Private Function NextWordStart(p As Integer) As Integer
            While p < _buf.Length AndAlso Char.IsLetterOrDigit(_buf(p))
                p += 1
            End While
            While p < _buf.Length AndAlso Not Char.IsLetterOrDigit(_buf(p)) AndAlso _buf(p) <> ChrW(10)
                p += 1
            End While
            Return p
        End Function

        ''' <summary>Just past the end of this or the next word (Vim's e, as a range end).</summary>
        Private Function WordEnd(p As Integer) As Integer
            If p < _buf.Length AndAlso Not Char.IsLetterOrDigit(_buf(p)) Then
                While p < _buf.Length AndAlso Not Char.IsLetterOrDigit(_buf(p))
                    p += 1
                End While
            End If
            While p < _buf.Length AndAlso Char.IsLetterOrDigit(_buf(p))
                p += 1
            End While
            Return p
        End Function

        Private Sub VimDelete(fromP As Integer, toP As Integer)
            fromP = Math.Max(0, Math.Min(fromP, _buf.Length))
            toP = Math.Max(fromP, Math.Min(toP, _buf.Length))
            If toP = fromP Then Return
            SaveUndo()
            _vimRegister = _buf.ToString(fromP, toP - fromP)
            _buf.Remove(fromP, toP - fromP)
            _pos = fromP
        End Sub

        ''' <summary>One key in normal mode: h j k l w b e 0 ^ $ gg G x X D C S s dd dw de db d$ d0 cc cw ce c$ yy yw y$ p P u i a I A o O.</summary>
        Private Function VimKey(ch As Char) As String
            Dim lineS = LineStart(_pos), lineE = LineEnd(_pos)
            If _vimPending IsNot Nothing Then
                Dim op = _vimPending
                _vimPending = Nothing
                If op = "g" Then
                    If ch = "g"c Then _pos = 0
                    Return Nothing
                End If
                Dim fromP = _pos, toP = _pos
                Select Case ch
                    Case op(0)
                        ' dd / cc / yy: the whole line (dd also takes its line break).
                        fromP = lineS
                        toP = lineE
                        If op = "d" Then
                            If lineE < _buf.Length Then
                                toP = lineE + 1
                            ElseIf lineS > 0 Then
                                fromP = lineS - 1
                            End If
                        End If
                    Case "w"c : toP = If(op = "c", WordEnd(_pos), NextWordStart(_pos))
                    Case "e"c : toP = WordEnd(_pos)
                    Case "b"c : fromP = WordLeft(_pos)
                    Case "$"c : toP = lineE
                    Case "0"c : fromP = lineS
                    Case Else : Return Nothing
                End Select
                If op = "y" Then
                    _vimRegister = _buf.ToString(fromP, toP - fromP)
                    Return Nothing
                End If
                VimDelete(fromP, toP)
                If op = "c" Then VimInsert()
                Return Nothing
            End If
            Select Case ch
                Case "h"c : If _pos > lineS Then _pos = PrevIndex(_pos)
                Case "l"c : If NextIndex(_pos) < lineE Then _pos = NextIndex(_pos)
                Case "j"c : If lineE < _buf.Length Then MoveVertical(1) Else HistoryMove(-1)
                Case "k"c : If lineS > 0 Then MoveVertical(-1) Else HistoryMove(1)
                Case "w"c : _pos = NextWordStart(_pos)
                Case "b"c : _pos = WordLeft(_pos)
                Case "e"c : _pos = Math.Max(_pos, WordEnd(NextIndex(_pos)) - 1)
                Case "0"c : _pos = lineS
                Case "^"c
                    _pos = lineS
                    While _pos < lineE AndAlso Char.IsWhiteSpace(_buf(_pos))
                        _pos += 1
                    End While
                Case "$"c : _pos = Math.Max(lineS, lineE - 1)
                Case "G"c : _pos = _buf.Length
                Case "g"c, "d"c, "c"c, "y"c : _vimPending = ch.ToString()
                Case "x"c : If _pos < lineE Then VimDelete(_pos, NextIndex(_pos))
                Case "X"c : If _pos > lineS Then VimDelete(PrevIndex(_pos), _pos)
                Case "D"c : VimDelete(_pos, lineE)
                Case "C"c
                    VimDelete(_pos, lineE)
                    VimInsert()
                Case "S"c
                    VimDelete(lineS, lineE)
                    VimInsert()
                Case "s"c
                    If _pos < lineE Then VimDelete(_pos, NextIndex(_pos))
                    VimInsert()
                Case "p"c
                    If _vimRegister.Length > 0 Then
                        SaveUndo()
                        Dim at = If(_pos < lineE, NextIndex(_pos), _pos)
                        _buf.Insert(at, _vimRegister)
                        _pos = at + _vimRegister.Length - 1
                    End If
                Case "P"c
                    If _vimRegister.Length > 0 Then
                        SaveUndo()
                        _buf.Insert(_pos, _vimRegister)
                    End If
                Case "u"c
                    If _undo.Count > 0 Then
                        Dim u = _undo.Pop()
                        _buf.Clear()
                        _buf.Append(u.Item1)
                        _pos = Math.Min(u.Item2, _buf.Length)
                    End If
                Case "i"c : VimInsert()
                Case "a"c
                    If _pos < lineE Then _pos = NextIndex(_pos)
                    VimInsert()
                Case "I"c
                    _pos = lineS
                    VimInsert()
                Case "A"c
                    _pos = lineE
                    VimInsert()
                Case "o"c
                    _pos = lineE
                    InsertText(vbLf, Nothing)
                    VimInsert()
                Case "O"c
                    _pos = lineS
                    InsertText(vbLf, Nothing)
                    _pos = lineS
                    VimInsert()
            End Select
            Return Nothing
        End Function

        ' ------------------------------------------------------------------ reverse history search (Ctrl+R)

        Private Function SearchKey(k As ConsoleKeyInfo, isKey As Func(Of String, Boolean)) As String
            If isKey("search-next") Then
                _searchFrom -= 1
                SearchApply()
            ElseIf isKey("search-cancel") Then
                _searching = False
                SetBuffer(_draft)
            ElseIf isKey("search-accept") Then
                _searching = False
            ElseIf k.Key = ConsoleKey.Backspace Then
                If _searchQuery.Length > 0 Then _searchQuery = _searchQuery.Substring(0, _searchQuery.Length - 1)
                _searchFrom = History.Count - 1
                SearchApply()
            ElseIf PlainCharacter(k) Then
                _searchQuery &= k.KeyChar
                SearchApply()
            End If
            Return Nothing
        End Function

        Private Sub SearchApply()
            For i = Math.Min(_searchFrom, History.Count - 1) To 0 Step -1
                If History(i).IndexOf(_searchQuery, StringComparison.OrdinalIgnoreCase) >= 0 Then
                    _searchFrom = i
                    SetBuffer(History(i))
                    Return
                End If
            Next
        End Sub

        ' ------------------------------------------------------------------ editing

        Private Sub SaveUndo()
            If _undo.Count = 0 OrElse _undo.Peek().Item1 <> _buf.ToString() Then _undo.Push((_buf.ToString(), _pos))
            If _undo.Count > 200 Then
                Dim keep = _undo.ToArray().Take(100).Reverse().ToList()
                _undo.Clear()
                For Each u In keep
                    _undo.Push(u)
                Next
            End If
        End Sub

        Private Sub InsertText(s As String, batch As StringBuilder)
            If Not AllowMultiline Then s = s.Replace(vbLf, " ")
            If batch Is Nothing OrElse batch.Length = 0 Then SaveUndo()
            _buf.Insert(_pos, s)
            _pos += s.Length
            batch?.Append(s)
        End Sub

        Private Sub SetBuffer(s As String)
            _buf.Clear()
            _buf.Append(s)
            _pos = _buf.Length
        End Sub

        Private Sub Backspace()
            If _pos = 0 Then Return
            SaveUndo()
            ' A paste placeholder goes as a whole.
            Dim before = _buf.ToString(0, _pos)
            Dim m = Regex.Match(before, "\[Pasted text #\d+ \+\d+ lines?\]$")
            If m.Success AndAlso Pastes.ContainsKey(m.Value) Then
                _buf.Remove(m.Index, m.Length)
                _pos = m.Index
                Pastes.Remove(m.Value)
                Return
            End If
            Dim p = PrevIndex(_pos)
            _buf.Remove(p, _pos - p)
            _pos = p
        End Sub

        Private Sub DeleteWordBack()
            If _pos = 0 Then Return
            SaveUndo()
            Dim p = WordLeft(_pos)
            _buf.Remove(p, _pos - p)
            _pos = p
        End Sub

        Private Function PrevIndex(p As Integer) As Integer
            If p <= 0 Then Return 0
            If p >= 2 AndAlso Char.IsLowSurrogate(_buf(p - 1)) AndAlso Char.IsHighSurrogate(_buf(p - 2)) Then Return p - 2
            Return p - 1
        End Function

        Private Function NextIndex(p As Integer) As Integer
            If p >= _buf.Length Then Return _buf.Length
            If Char.IsHighSurrogate(_buf(p)) AndAlso p + 1 < _buf.Length Then Return p + 2
            Return p + 1
        End Function

        Private Function WordLeft(p As Integer) As Integer
            While p > 0 AndAlso Not Char.IsLetterOrDigit(_buf(p - 1))
                p -= 1
            End While
            While p > 0 AndAlso Char.IsLetterOrDigit(_buf(p - 1))
                p -= 1
            End While
            Return p
        End Function

        Private Function WordRight(p As Integer) As Integer
            While p < _buf.Length AndAlso Not Char.IsLetterOrDigit(_buf(p))
                p += 1
            End While
            While p < _buf.Length AndAlso Char.IsLetterOrDigit(_buf(p))
                p += 1
            End While
            Return p
        End Function

        Private Function LineStart(p As Integer) As Integer
            While p > 0 AndAlso _buf(p - 1) <> ChrW(10)
                p -= 1
            End While
            Return p
        End Function

        Private Function LineEnd(p As Integer) As Integer
            While p < _buf.Length AndAlso _buf(p) <> ChrW(10)
                p += 1
            End While
            Return p
        End Function

        Private Sub MoveVertical(direction As Integer)
            Dim col = _pos - LineStart(_pos)
            If direction < 0 Then
                Dim prevEnd = LineStart(_pos) - 1
                Dim prevStart = LineStart(prevEnd)
                _pos = Math.Min(prevStart + col, prevEnd)
            Else
                Dim nextStart = LineEnd(_pos) + 1
                Dim nextEnd = LineEnd(nextStart)
                _pos = Math.Min(nextStart + col, nextEnd)
            End If
        End Sub

        Private Sub HistoryMove(direction As Integer)
            If History.Count = 0 Then Return
            If _historyIndex = -1 Then
                If direction < 0 Then Return
                _draft = _buf.ToString()
            End If
            Dim nextIndex = _historyIndex + direction
            If nextIndex < 0 Then
                _historyIndex = -1
                SetBuffer(_draft)
                Return
            End If
            If nextIndex >= History.Count Then Return
            _historyIndex = nextIndex
            SetBuffer(History(History.Count - 1 - _historyIndex))
        End Sub

        Private Sub CollapsePaste(start As Integer, inserted As String)
            Dim lines = inserted.Count(Function(c) c = ChrW(10)) + 1
            If inserted.Length < MaxPasteChars AndAlso lines <= 12 Then Return
            If start < 0 OrElse start + inserted.Length > _buf.Length OrElse _buf.ToString(start, inserted.Length) <> inserted Then Return
            _pasteCount += 1
            Dim key = $"[Pasted text #{_pasteCount} +{lines} line{If(lines = 1, "", "s")}]"
            Pastes(key) = inserted
            _buf.Remove(start, inserted.Length)
            _buf.Insert(start, key)
            _pos = start + key.Length
        End Sub

        Public Function Expand(text As String) As String
            For Each kv In Pastes
                text = text.Replace(kv.Key, kv.Value)
            Next
            Return text
        End Function

        ' ------------------------------------------------------------------ suggestions

        Private Function SuggestionsVisible() As Boolean
            Return Not _suggestHidden AndAlso _suggestions.Count > 0
        End Function

        Private Sub RefreshSuggestions()
            If Suggest Is Nothing OrElse _suggestHidden OrElse Secret Then
                _suggestions.Clear()
                Return
            End If
            Dim previous = If(_suggestions.Count > 0 AndAlso _suggestIndex < _suggestions.Count, _suggestions(_suggestIndex).Label, Nothing)
            Dim r = Suggest.Invoke(_buf.ToString(), _pos)
            _suggestions = If(r.Items, New List(Of Suggestion))
            _suggestStart = Math.Max(0, Math.Min(r.Start, _pos))
            _suggestIndex = Math.Max(0, _suggestions.FindIndex(Function(s) s.Label = previous))
        End Sub

        Private Sub AcceptSuggestion()
            If _suggestions.Count = 0 Then Return
            Dim s = _suggestions(_suggestIndex)
            SaveUndo()
            ' Replace the word being completed (up to the next space).
            Dim tokenEnd = _pos
            While tokenEnd < _buf.Length AndAlso Not Char.IsWhiteSpace(_buf(tokenEnd))
                tokenEnd += 1
            End While
            _buf.Remove(_suggestStart, tokenEnd - _suggestStart)
            _buf.Insert(_suggestStart, s.Text)
            _pos = _suggestStart + s.Text.Length
            _suggestions.Clear()
        End Sub

        ' ------------------------------------------------------------------ rendering

        Private Function Finish() As String
            Dim text = _buf.ToString()
            ClearFrame()
            If Not Boxed Then
                _term.Write(Prompt & If(Secret, New String("•"c, Math.Min(12, text.Length)), text) & Environment.NewLine)
            End If
            If text.Trim().Length > 0 AndAlso Not Secret Then
                History.RemoveAll(Function(h) h = text)
                History.Add(text)
            End If
            Return Expand(text)
        End Function

        ''' <summary>Prints text above the box (used by shortcuts while editing); the box is drawn again below it.</summary>
        Public Sub PrintAbove(text As String)
            ClearFrame()
            _term.Write(text)
        End Sub

        ''' <summary>Shows a short note in the footer until the next key.</summary>
        Public Sub Flash(note As String)
            _hint = note
        End Sub

        ''' <summary>Removes the box from the screen (the cursor ends where it started).</summary>
        Public Sub ClearFrame()
            If _rendered Then _term.Write(ChrW(13) & Ansi.Up(_rows) & Ansi.ClearDown)
            _rows = 0
            _rendered = False
        End Sub

        Private Sub Render()
            RefreshSuggestions()
            Dim cols = Math.Max(20, _term.Columns - 1)
            Dim inner = If(Boxed, cols - 4, cols)
            Dim promptW = Ansi.Width(Prompt)
            Dim textW = Math.Max(8, inner - promptW)
            ' Wrap the buffer into rows, finding the cursor's row and column.
            Dim rows As New List(Of String)
            Dim cur As New StringBuilder()
            Dim curW = 0
            Dim cursorRow = 0, cursorCol = 0
            Dim shown = If(Secret, New String("•"c, _buf.Length), _buf.ToString())
            Dim i = 0
            While i <= shown.Length
                If i = _pos Then
                    cursorRow = rows.Count
                    cursorCol = curW
                End If
                If i = shown.Length Then Exit While
                Dim ch = shown(i)
                If ch = ChrW(10) Then
                    rows.Add(cur.ToString())
                    cur.Clear()
                    curW = 0
                    i += 1
                    Continue While
                End If
                Dim len = If(Char.IsHighSurrogate(ch) AndAlso i + 1 < shown.Length, 2, 1)
                Dim cw = Ansi.CharWidth(If(len = 2, Char.ConvertToUtf32(ch, shown(i + 1)), AscW(ch)))
                If curW + cw > textW Then
                    rows.Add(cur.ToString())
                    cur.Clear()
                    curW = 0
                    If i = _pos Then
                        cursorRow = rows.Count
                        cursorCol = 0
                    End If
                End If
                cur.Append(shown, i, len)
                curW += cw
                i += len
            End While
            rows.Add(cur.ToString())
            If cursorCol >= textW Then
                ' The cursor sits after a full row: show it at the start of the next.
                cursorRow += 1
                cursorCol = 0
                If cursorRow >= rows.Count Then rows.Add("")
            End If
            ' Very tall input: show a window around the cursor.
            Dim maxRows = Math.Max(3, _term.Lines - 8 - Math.Min(8, _suggestions.Count))
            Dim first = 0
            If rows.Count > maxRows Then
                first = Math.Max(0, Math.Min(cursorRow - maxRows \ 2, rows.Count - maxRows))
                rows = rows.Skip(first).Take(maxRows).ToList()
                cursorRow -= first
            End If

            Dim frame As New List(Of String)
            Dim box = Ansi.Box
            Dim border = Function(s As String) If(_searching, Ansi.Warn(s), Ansi.Dim2(s))
            If Boxed Then frame.Add(border(box(0) & New String(box(4)(0), cols - 2) & box(1)))
            For r = 0 To rows.Count - 1
                Dim prefix = If(r = 0 AndAlso first = 0, Ansi.Accent(Prompt), New String(" "c, promptW))
                Dim content = rows(r)
                If r = 0 AndAlso _buf.Length = 0 AndAlso Not String.IsNullOrEmpty(Placeholder) Then content = Ansi.Dim2(Ansi.Truncate(Placeholder, textW))
                Dim line = prefix & content
                If Boxed Then line = border(box(5)) & " " & Ansi.Pad(line, inner) & " " & border(box(5))
                frame.Add(line)
            Next
            If Boxed Then frame.Add(border(box(2) & New String(box(4)(0), cols - 2) & box(3)))
            Dim cursorFrameRow = If(Boxed, 1, 0) + cursorRow
            Dim cursorFrameCol = If(Boxed, 2, 0) + promptW + cursorCol

            ' Footer: search prompt, a hint, or the status (mode on the left, model/context on the right).
            If _searching Then
                frame.Add(Ansi.Warn($"  (reverse-i-search) '{_searchQuery}'") & Ansi.Muted($"  {KeyBindings.Label("search-accept")} to use · {KeyBindings.Label("search-next")} older · {KeyBindings.Label("search-cancel")} to cancel"))
            ElseIf Boxed Then
                Dim left = If(_hint IsNot Nothing, Ansi.Warn(_hint), If(Footer IsNot Nothing, Footer.Invoke(), ""))
                Dim right = If(FooterRight IsNot Nothing, FooterRight.Invoke(), "")
                Dim gap = cols - 2 - Ansi.Width(left) - Ansi.Width(right)
                If gap < 2 Then right = Ansi.Truncate(right, Math.Max(0, cols - 4 - Ansi.Width(left))) : gap = 2
                frame.Add("  " & left & New String(" "c, Math.Max(2, gap)) & right)
            ElseIf _hint IsNot Nothing Then
                frame.Add(Ansi.Warn("  " & _hint))
            End If
            If SuggestionsVisible() Then
                Dim labelW = Math.Min(30, _suggestions.Take(8).Max(Function(s) Ansi.Width(s.Label)) + 2)
                Dim startAt = Math.Max(0, Math.Min(_suggestIndex - 3, _suggestions.Count - 8))
                For si = startAt To Math.Min(_suggestions.Count - 1, startAt + 7)
                    Dim s = _suggestions(si)
                    Dim label = Ansi.Pad(Ansi.Truncate(s.Label, labelW), labelW)
                    Dim text = If(si = _suggestIndex, Ansi.Accent(label) & "  " & Ansi.Text(If(s.Hint, "")), Ansi.Muted(label) & "  " & Ansi.Dim2(If(s.Hint, "")))
                    frame.Add(Ansi.Truncate("  " & text, cols))
                Next
            End If

            Dim sb As New StringBuilder()
            If _rendered Then sb.Append(ChrW(13)).Append(Ansi.Up(_rows)).Append(Ansi.ClearDown) Else sb.Append(ChrW(13)).Append(Ansi.ClearDown)
            For r = 0 To frame.Count - 1
                sb.Append(frame(r))
                If r < frame.Count - 1 Then sb.Append(Environment.NewLine)
            Next
            ' Put the cursor in the text.
            sb.Append(ChrW(13)).Append(Ansi.Up(frame.Count - 1 - cursorFrameRow)).Append(Ansi.Right(cursorFrameCol))
            _term.Write(sb.ToString())
            _rows = cursorFrameRow
            _rendered = True
        End Sub

    End Class

End Namespace
