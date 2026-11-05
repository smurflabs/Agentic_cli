Option Strict On
Option Explicit On
Option Infer On

Imports System.Text
Imports System.Threading

Namespace Ui

    ''' <summary>The terminal as the line editor and menus see it (a fake one replays keys in the self-test).</summary>
    Friend Interface ITerminal
        Function ReadKey() As ConsoleKeyInfo
        ReadOnly Property KeyAvailable As Boolean
        Sub Write(text As String)
        ReadOnly Property Columns As Integer
        ReadOnly Property Lines As Integer
    End Interface

    Friend NotInheritable Class RealTerminal
        Implements ITerminal

        Public Function ReadKey() As ConsoleKeyInfo Implements ITerminal.ReadKey
            Return Console.ReadKey(True)
        End Function

        Public ReadOnly Property KeyAvailable As Boolean Implements ITerminal.KeyAvailable
            Get
                Try
                    Return Console.KeyAvailable
                Catch ex As InvalidOperationException
                    Return False
                End Try
            End Get
        End Property

        Public Sub Write(text As String) Implements ITerminal.Write
            Out.Raw(text)
        End Sub

        Public ReadOnly Property Columns As Integer Implements ITerminal.Columns
            Get
                Return Ansi.TermWidth()
            End Get
        End Property

        Public ReadOnly Property Lines As Integer Implements ITerminal.Lines
            Get
                Return Ansi.TermHeight()
            End Get
        End Property

    End Class

    ''' <summary>Replays a script of keys; everything written is collected.</summary>
    Friend NotInheritable Class FakeTerminal
        Implements ITerminal

        Public ReadOnly Keys As New Queue(Of ConsoleKeyInfo)
        Public ReadOnly Output As New StringBuilder()
        Public Property Width As Integer = 80
        Public Property Height As Integer = 24

        Public Sub Type(text As String)
            For Each c In text
                If c = ChrW(10) Then
                    Keys.Enqueue(New ConsoleKeyInfo(ChrW(13), ConsoleKey.Enter, False, False, False))
                Else
                    Keys.Enqueue(New ConsoleKeyInfo(c, KeyFor(c), Char.IsUpper(c), False, False))
                End If
            Next
        End Sub

        Public Sub Press(key As ConsoleKey, Optional shift As Boolean = False, Optional ctrl As Boolean = False, Optional ch As Char = ChrW(0))
            Keys.Enqueue(New ConsoleKeyInfo(ch, key, shift, False, ctrl))
        End Sub

        Private Shared Function KeyFor(c As Char) As ConsoleKey
            If c >= "a"c AndAlso c <= "z"c Then Return CType(AscW(Char.ToUpperInvariant(c)), ConsoleKey)
            If c >= "A"c AndAlso c <= "Z"c Then Return CType(AscW(c), ConsoleKey)
            If c >= "0"c AndAlso c <= "9"c Then Return CType(AscW(c), ConsoleKey)
            If c = " "c Then Return ConsoleKey.Spacebar
            Return ConsoleKey.Oem1
        End Function

        ''' <summary>Output length when each F22 "mark" key was read (what the screen showed at that point).</summary>
        Public ReadOnly Marks As New List(Of Integer)
        Private _exhausted As Integer

        Public Function ReadKey() As ConsoleKeyInfo Implements ITerminal.ReadKey
            If Keys.Count = 0 Then
                ' End of the script: Ctrl+U then Ctrl+D (exit); a script that never ends is an error.
                _exhausted += 1
                If _exhausted > 20 Then Throw New InvalidOperationException("The key script ended but the editor kept reading.")
                Return If(_exhausted Mod 2 = 1, New ConsoleKeyInfo(ChrW(21), ConsoleKey.U, False, False, True), New ConsoleKeyInfo(ChrW(4), ConsoleKey.D, False, False, True))
            End If
            Dim k = Keys.Dequeue()
            If k.Key = ConsoleKey.F22 Then Marks.Add(Output.Length)
            Return k
        End Function

        Public ReadOnly Property KeyAvailable As Boolean Implements ITerminal.KeyAvailable
            Get
                Return Keys.Count > 0 AndAlso Keys.Peek().Key <> ConsoleKey.F24 AndAlso Keys.Peek().Key <> ConsoleKey.F22
            End Get
        End Property

        Public Sub Write(text As String) Implements ITerminal.Write
            Output.Append(text)
        End Sub

        Public ReadOnly Property Columns As Integer Implements ITerminal.Columns
            Get
                Return Width
            End Get
        End Property

        Public ReadOnly Property Lines As Integer Implements ITerminal.Lines
            Get
                Return Height
            End Get
        End Property

    End Class

    ''' <summary>
    ''' All console output goes through here, so the spinner (one line at the bottom, redrawn by a timer) never collides with
    ''' text: before anything is written the spinner line is cleared, and it comes back on the next tick.
    ''' </summary>
    Friend NotInheritable Class Out

        Private Shared ReadOnly Gate As New Object()
        Private Shared _spinnerOn As Boolean
        Private Shared _spinnerShown As Boolean
        Private Shared _spinnerText As String = ""
        Private Shared _spinnerMeta As Func(Of String)
        Private Shared _spinnerStarted As DateTime
        Private Shared _spinnerTimer As Timer
        Private Shared _frame As Integer
        Private Shared _atLineStart As Boolean = True
        Private Shared _extra As String = ""
        ''' <summary>Lines shown above the spinner while it runs (the model's thinking as it happens), redrawn in place.</summary>
        Private Shared _panel As String()
        ''' <summary>How many panel rows are on the screen above the spinner line now.</summary>
        Private Shared _panelRows As Integer
        Public Shared Property Silent As Boolean

        ''' <summary>Writes text as is (used by the editor and menus, which manage the cursor themselves).</summary>
        Public Shared Sub Raw(text As String)
            If Silent OrElse String.IsNullOrEmpty(text) Then Return
            SyncLock Gate
                HideSpinnerLocked()
                Console.Out.Write(text)
                Console.Out.Flush()
                _atLineStart = text.EndsWith(ChrW(10))
            End SyncLock
        End Sub

        Public Shared Sub Write(text As String)
            Raw(text)
        End Sub

        Public Shared Sub Line(Optional text As String = "")
            Raw(text & Environment.NewLine)
        End Sub

        ''' <summary>Several lines at once (each wrapped to the terminal width with the given indent).</summary>
        Public Shared Sub Block(text As String, Optional indent As String = "")
            Dim sb As New StringBuilder()
            Dim w = Ansi.TermWidth()
            For Each l In text.Replace(ChrW(13), "").Split(ChrW(10))
                For Each wrapped In Ansi.WrapText(l, w - 1, indent, indent)
                    sb.Append(wrapped).Append(Environment.NewLine)
                Next
            Next
            Raw(sb.ToString())
        End Sub

        Public Shared Sub Note(text As String)
            Line(Ansi.Muted(text))
        End Sub

        Public Shared Sub Success(text As String)
            Line(Ansi.Ok(Ansi.Dot & " ") & text)
        End Sub

        Public Shared Sub Warning(text As String)
            Line(Ansi.Warn(Ansi.Dot & " " & text))
        End Sub

        Public Shared Sub Fail(text As String)
            Line(Ansi.Bad(Ansi.Dot & " " & text))
        End Sub

        Public Shared Sub EnsureNewLine()
            SyncLock Gate
                If Not _atLineStart AndAlso Not Silent Then
                    HideSpinnerLocked()
                    Console.Out.Write(Environment.NewLine)
                    _atLineStart = True
                End If
            End SyncLock
        End Sub

        ' ------------------------------------------------------------------ spinner

        Public Shared ReadOnly Property SpinnerActive As Boolean
            Get
                Return _spinnerOn
            End Get
        End Property

        ''' <summary>Shows a working line: glyph, text, elapsed time and the meta supplier's text (tokens, hint).</summary>
        Public Shared Sub StartSpinner(text As String, Optional meta As Func(Of String) = Nothing)
            If Not Ansi.Interactive OrElse Not Ansi.Vt OrElse Silent Then Return
            SyncLock Gate
                _spinnerText = text
                _spinnerMeta = meta
                If Not _spinnerOn Then _spinnerStarted = DateTime.UtcNow
                _spinnerOn = True
                If _spinnerTimer Is Nothing Then _spinnerTimer = New Timer(AddressOf Tick, Nothing, 0, 110)
            End SyncLock
        End Sub

        Public Shared Sub SpinnerText(text As String)
            SyncLock Gate
                _spinnerText = text
            End SyncLock
        End Sub

        ''' <summary>Text shown after the spinner line's meta (e.g. what you are typing during an answer).</summary>
        Public Shared Sub SpinnerExtra(text As String)
            SyncLock Gate
                _extra = If(text, "")
            End SyncLock
            Tick(Nothing)
        End Sub

        Public Shared Sub StopSpinner()
            SyncLock Gate
                _spinnerOn = False
                _extra = ""
                HideSpinnerLocked()
                _spinnerTimer?.Dispose()
                _spinnerTimer = Nothing
            End SyncLock
        End Sub

        ''' <summary>
        ''' Lines to show above the spinner (each kept to one terminal row), or Nothing for none. They are drawn with the next
        ''' spinner frame and disappear before anything else is printed, so they never mix with the answer.
        ''' </summary>
        Public Shared Sub SetPanel(lines As IList(Of String))
            SyncLock Gate
                _panel = If(lines Is Nothing OrElse lines.Count = 0, Nothing, lines.ToArray())
            End SyncLock
        End Sub

        ''' <summary>Removes the panel from the screen now.</summary>
        Public Shared Sub ClearPanel()
            SyncLock Gate
                HideSpinnerLocked()
                _panel = Nothing
            End SyncLock
        End Sub

        Public Shared ReadOnly Property SpinnerElapsed As TimeSpan
            Get
                Return If(_spinnerOn, DateTime.UtcNow - _spinnerStarted, TimeSpan.Zero)
            End Get
        End Property

        Private Shared Sub HideSpinnerLocked()
            If _spinnerShown Then
                Console.Out.Write(ChrW(13) & If(_panelRows > 0, Ansi.Up(_panelRows) & Ansi.ClearDown, Ansi.ClearLine))
                _spinnerShown = False
                _panelRows = 0
            End If
        End Sub

        Private Shared Sub Tick(state As Object)
            SyncLock Gate
                If Not _spinnerOn OrElse Silent OrElse Not _atLineStart Then Return
                _frame += 1
                Dim glyphs = Ansi.SpinGlyphs
                ' Glyphs breathe back and forth like Claude's.
                Dim cycle = glyphs.Length * 2 - 2
                Dim k = If(cycle <= 0, 0, _frame \ 2 Mod cycle)
                Dim g = glyphs(If(k < glyphs.Length, k, cycle - k))
                Dim secs = CInt((DateTime.UtcNow - _spinnerStarted).TotalSeconds)
                Dim elapsed = If(secs < 60, secs & "s", (secs \ 60) & "m " & (secs Mod 60).ToString("00") & "s")
                Dim meta = If(_spinnerMeta IsNot Nothing, _spinnerMeta(), "")
                Dim shimmered = Shimmer(_spinnerText, _frame)
                Dim lineText = Ansi.Brand(g) & " " & shimmered & " " & Ansi.Muted("(" & elapsed & If(String.IsNullOrEmpty(meta), "", " · " & meta) & ")") & _extra
                Dim w = Ansi.TermWidth()
                Dim frameText As New StringBuilder()
                frameText.Append(ChrW(13)).Append(If(_spinnerShown AndAlso _panelRows > 0, Ansi.Up(_panelRows) & Ansi.ClearDown, Ansi.ClearLine))
                Dim rows = 0
                If _panel IsNot Nothing Then
                    ' Never taller than the terminal allows (the frame must be redrawn in place).
                    Dim room = Math.Max(0, Math.Min(_panel.Length, Ansi.TermHeight() - 3))
                    For Each p In _panel.Skip(_panel.Length - room)
                        frameText.Append(Ansi.Truncate(p, w - 1)).Append(ChrW(13)).Append(ChrW(10))
                        rows += 1
                    Next
                End If
                frameText.Append(Ansi.Truncate(lineText, w - 1))
                Console.Out.Write(frameText.ToString())
                Console.Out.Flush()
                _panelRows = rows
                _spinnerShown = True
            End SyncLock
        End Sub

        ''' <summary>The working word with a light sweeping across it.</summary>
        Private Shared Function Shimmer(text As String, frame As Integer) As String
            If Not Ansi.Colors OrElse String.IsNullOrEmpty(text) Then Return text
            Dim pos = (frame \ 1) Mod (text.Length + 8) - 4
            Dim sb As New StringBuilder()
            For i = 0 To text.Length - 1
                Dim d = Math.Abs(i - pos)
                Dim color = If(d = 0, 225, If(d = 1, 183, 177))
                sb.Append(Ansi.Fg(color, text(i).ToString()))
            Next
            Return sb.ToString()
        End Function

    End Class

End Namespace
