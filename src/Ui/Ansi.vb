Option Strict On
Option Explicit On
Option Infer On

Imports System.Runtime.InteropServices
Imports System.Text
Imports System.Text.RegularExpressions

Namespace Ui

    ''' <summary>
    ''' Terminal styling: 256-colour ANSI sequences (enabled on Windows with ENABLE_VIRTUAL_TERMINAL_PROCESSING), display width of
    ''' text, ANSI-aware wrapping, and the glyph set (rich Unicode in Windows Terminal / VS Code, safe ASCII-ish in old consoles).
    ''' </summary>
    Friend NotInheritable Class Ansi

        Public Shared ReadOnly E As String = ChrW(27)
        Private Shared _colors As Boolean = True
        Private Shared _vt As Boolean = True
        Private Shared _interactive As Boolean = True
        Private Shared _rich As Boolean = True

        ''' <summary>Colours and cursor control are allowed (a terminal, NO_COLOR not set).</summary>
        Public Shared ReadOnly Property Colors As Boolean
            Get
                Return _colors
            End Get
        End Property

        ''' <summary>Input and output are a terminal: the line editor, menus and the spinner work.</summary>
        ''' <summary>Cursor movement and line clearing work (a terminal with VT processing), whether or not colours are wanted.</summary>
        Public Shared ReadOnly Property Vt As Boolean
            Get
                Return _vt
            End Get
        End Property

        Public Shared ReadOnly Property Interactive As Boolean
            Get
                Return _interactive
            End Get
        End Property

        Public Shared ReadOnly Property Rich As Boolean
            Get
                Return _rich
            End Get
        End Property

        <DllImport("kernel32.dll", SetLastError:=True)>
        Private Shared Function GetStdHandle(nStdHandle As Integer) As IntPtr
        End Function

        <DllImport("kernel32.dll", SetLastError:=True)>
        Private Shared Function GetConsoleMode(hConsoleHandle As IntPtr, ByRef lpMode As UInteger) As Boolean
        End Function

        <DllImport("kernel32.dll", SetLastError:=True)>
        Private Shared Function SetConsoleMode(hConsoleHandle As IntPtr, dwMode As UInteger) As Boolean
        End Function

        Public Shared Sub Init(Optional forceColor As Boolean? = Nothing, Optional glyphs As String = Nothing)
            Try
                Console.OutputEncoding = New UTF8Encoding(False)
                Console.InputEncoding = New UTF8Encoding(False)
            Catch ex As Exception When TypeOf ex Is IO.IOException OrElse TypeOf ex Is InvalidOperationException OrElse TypeOf ex Is PlatformNotSupportedException
            End Try
            _interactive = Not Console.IsInputRedirected AndAlso Not Console.IsOutputRedirected
            Dim vt = True
            If OperatingSystem.IsWindows() AndAlso Not Console.IsOutputRedirected Then
                Try
                    Dim h = GetStdHandle(-11)
                    Dim mode As UInteger
                    If GetConsoleMode(h, mode) Then vt = SetConsoleMode(h, mode Or &H4UI) OrElse (mode And &H4UI) <> 0
                Catch ex As Exception When TypeOf ex Is DllNotFoundException OrElse TypeOf ex Is EntryPointNotFoundException
                End Try
            End If
            _vt = vt AndAlso Not Console.IsOutputRedirected
            _colors = _vt AndAlso String.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR"))
            If forceColor.HasValue Then _colors = forceColor.Value
            If Not String.IsNullOrEmpty(Environment.GetEnvironmentVariable("FORCE_COLOR")) Then _colors = True
            ' Old consoles (conhost with raster or Consolas fonts) lack ⎿ ✻ ╭ and friends; Windows Terminal and VS Code have them.
            Dim modern = Not String.IsNullOrEmpty(Environment.GetEnvironmentVariable("WT_SESSION")) OrElse
                         Not String.IsNullOrEmpty(Environment.GetEnvironmentVariable("TERM_PROGRAM")) OrElse
                         Not OperatingSystem.IsWindows()
            Select Case If(glyphs, "").ToLowerInvariant()
                Case "unicode", "rich" : _rich = True
                Case "ascii", "safe", "simple" : _rich = False
                Case Else : _rich = modern
            End Select
        End Sub

        ''' <summary>For tests: a non-terminal with or without colours.</summary>
        Friend Shared Sub InitForTest(colors As Boolean, rich As Boolean)
            _colors = colors
            _rich = rich
            _vt = True
            _interactive = True
        End Sub

        ' ------------------------------------------------------------------ styles

        Private Shared Function Wrap(code As String, text As String, reset As String) As String
            If Not _colors OrElse String.IsNullOrEmpty(text) Then Return text
            Return E & "[" & code & "m" & text & E & "[" & reset & "m"
        End Function

        Public Shared Function Fg(color As Integer, text As String) As String
            Return Wrap("38;5;" & color, text, "39")
        End Function

        Public Shared Function Bg(color As Integer, text As String) As String
            Return Wrap("48;5;" & color, text, "49")
        End Function

        Public Shared Function Bold(text As String) As String
            Return Wrap("1", text, "22")
        End Function

        Public Shared Function Faint(text As String) As String
            Return Wrap("2", text, "22")
        End Function

        Public Shared Function Italic(text As String) As String
            Return Wrap("3", text, "23")
        End Function

        Public Shared Function Underline(text As String) As String
            Return Wrap("4", text, "24")
        End Function

        Public Shared Function Strike(text As String) As String
            Return Wrap("9", text, "29")
        End Function

        Public Shared Function Inverse(text As String) As String
            Return Wrap("7", text, "27")
        End Function

        ' The palette (256 colours): the web app's violet accent, readable greys, status colours.
        Public Shared Function Accent(t As String) As String
            Return Fg(141, t)
        End Function
        Public Shared Function Brand(t As String) As String
            Return Fg(177, t)
        End Function
        Public Shared Function Muted(t As String) As String
            Return Fg(245, t)
        End Function
        Public Shared Function Dim2(t As String) As String
            Return Fg(240, t)
        End Function
        Public Shared Function Ok(t As String) As String
            Return Fg(114, t)
        End Function
        Public Shared Function Warn(t As String) As String
            Return Fg(214, t)
        End Function
        Public Shared Function Bad(t As String) As String
            Return Fg(203, t)
        End Function
        Public Shared Function Info(t As String) As String
            Return Fg(75, t)
        End Function
        Public Shared Function CodeColor(t As String) As String
            Return Fg(117, t)
        End Function
        Public Shared Function Text(t As String) As String
            Return Fg(252, t)
        End Function

        ' ------------------------------------------------------------------ cursor

        Public Shared Function Up(n As Integer) As String
            Return If(n <= 0 OrElse Not _vt, "", E & "[" & n & "A")
        End Function
        Public Shared Function Down(n As Integer) As String
            Return If(n <= 0 OrElse Not _vt, "", E & "[" & n & "B")
        End Function
        Public Shared Function Right(n As Integer) As String
            Return If(n <= 0 OrElse Not _vt, "", E & "[" & n & "C")
        End Function
        Public Shared Function Column(n As Integer) As String
            Return If(Not _vt, "", E & "[" & Math.Max(1, n) & "G")
        End Function
        Public Shared ReadOnly Property ClearLine As String
            Get
                Return If(_vt, E & "[2K", "")
            End Get
        End Property
        Public Shared ReadOnly Property ClearDown As String
            Get
                Return If(_vt, E & "[J", "")
            End Get
        End Property
        Public Shared ReadOnly Property HideCursor As String
            Get
                Return If(_vt, E & "[?25l", "")
            End Get
        End Property
        Public Shared ReadOnly Property ShowCursor As String
            Get
                Return If(_vt, E & "[?25h", "")
            End Get
        End Property

        Public Shared Function Title(t As String) As String
            Return If(_vt, E & "]0;" & t & ChrW(7), "")
        End Function

        ' ------------------------------------------------------------------ glyphs

        Public Shared ReadOnly Property Dot As String
            Get
                Return If(_rich, "●", "*")
            End Get
        End Property
        Public Shared ReadOnly Property Elbow As String
            Get
                Return If(_rich, "⎿", "└")
            End Get
        End Property
        ''' <summary>A subagent started (it runs alongside others).</summary>
        Public Shared ReadOnly Property Diamond As String
            Get
                Return If(_rich, "◇", "+")
            End Get
        End Property
        Public Shared ReadOnly Property Pointer As String
            Get
                Return If(_rich, "❯", ">")
            End Get
        End Property
        Public Shared ReadOnly Property CheckOn As String
            Get
                Return If(_rich, "☒", "[x]")
            End Get
        End Property
        Public Shared ReadOnly Property CheckOff As String
            Get
                Return If(_rich, "☐", "[ ]")
            End Get
        End Property
        Public Shared ReadOnly Property Ellipsis As String
            Get
                Return "…"
            End Get
        End Property
        Public Shared ReadOnly Property SpinGlyphs As String()
            Get
                Return If(_rich, {"·", "✢", "✳", "✶", "✻", "✽"}, {"-", "\", "|", "/"})
            End Get
        End Property
        Public Shared ReadOnly Property Star As String
            Get
                Return If(_rich, "✻", "*")
            End Get
        End Property
        Public Shared ReadOnly Property Therefore As String
            Get
                Return If(_rich, "∴", ":")
            End Get
        End Property
        ''' <summary>Box corners: top-left, top-right, bottom-left, bottom-right, horizontal, vertical.</summary>
        Public Shared ReadOnly Property Box As String()
            Get
                Return If(_rich, {"╭", "╮", "╰", "╯", "─", "│"}, {"┌", "┐", "└", "┘", "─", "│"})
            End Get
        End Property

        ' ------------------------------------------------------------------ width and wrapping

        Private Shared ReadOnly AnsiPattern As New Regex(ChrW(27) & "(\[[0-9;?]*[ -/]*[@-~]|\][^" & ChrW(7) & "]*" & ChrW(7) & ")", RegexOptions.Compiled)

        Public Shared Function Strip(t As String) As String
            If String.IsNullOrEmpty(t) Then Return ""
            Return AnsiPattern.Replace(t, "")
        End Function

        ''' <summary>Columns a character takes: 2 for wide East Asian characters and emoji, 0 for combining marks.</summary>
        Public Shared Function CharWidth(cp As Integer) As Integer
            If cp = 0 Then Return 0
            If cp < 32 OrElse (cp >= &H7F AndAlso cp < &HA0) Then Return 0
            If (cp >= &H300 AndAlso cp <= &H36F) OrElse (cp >= &H200B AndAlso cp <= &H200F) OrElse cp = &HFE0F OrElse cp = &HFE0E Then Return 0
            If (cp >= &H1100 AndAlso cp <= &H115F) OrElse (cp >= &H2E80 AndAlso cp <= &HA4CF AndAlso cp <> &H303F) OrElse
               (cp >= &HAC00 AndAlso cp <= &HD7A3) OrElse (cp >= &HF900 AndAlso cp <= &HFAFF) OrElse (cp >= &HFE30 AndAlso cp <= &HFE4F) OrElse
               (cp >= &HFF00 AndAlso cp <= &HFF60) OrElse (cp >= &HFFE0 AndAlso cp <= &HFFE6) OrElse (cp >= &H1F300 AndAlso cp <= &H1FAFF) OrElse
               (cp >= &H20000 AndAlso cp <= &H3FFFD) Then Return 2
            Return 1
        End Function

        ''' <summary>Display width of text (ANSI sequences ignored).</summary>
        Public Shared Function Width(t As String) As Integer
            Dim s = Strip(t)
            Dim w = 0
            Dim i = 0
            While i < s.Length
                Dim cp As Integer
                If Char.IsHighSurrogate(s(i)) AndAlso i + 1 < s.Length Then
                    cp = Char.ConvertToUtf32(s(i), s(i + 1))
                    i += 2
                Else
                    cp = AscW(s(i))
                    i += 1
                End If
                w += CharWidth(cp)
            End While
            Return w
        End Function

        Public Shared Function TermWidth() As Integer
            Try
                If Not Console.IsOutputRedirected Then
                    Dim w = Console.WindowWidth
                    If w > 10 Then Return w
                End If
            Catch ex As Exception When TypeOf ex Is IO.IOException OrElse TypeOf ex Is PlatformNotSupportedException
            End Try
            Dim cols As Integer
            If Integer.TryParse(Environment.GetEnvironmentVariable("COLUMNS"), cols) AndAlso cols > 10 Then Return cols
            Return 100
        End Function

        Public Shared Function TermHeight() As Integer
            Try
                If Not Console.IsOutputRedirected Then
                    Dim h = Console.WindowHeight
                    If h > 4 Then Return h
                End If
            Catch ex As Exception When TypeOf ex Is IO.IOException OrElse TypeOf ex Is PlatformNotSupportedException
            End Try
            Return 40
        End Function

        ''' <summary>Cuts text to a display width (ANSI-aware), adding … when cut.</summary>
        Public Shared Function Truncate(t As String, maxWidth As Integer) As String
            If maxWidth <= 0 Then Return ""
            If Width(t) <= maxWidth Then Return t
            Dim sb As New StringBuilder()
            Dim w = 0
            Dim i = 0
            While i < t.Length
                If t(i) = ChrW(27) Then
                    Dim m = AnsiPattern.Match(t, i)
                    If m.Success AndAlso m.Index = i Then
                        sb.Append(m.Value)
                        i += m.Length
                        Continue While
                    End If
                End If
                Dim cp As Integer
                Dim len = 1
                If Char.IsHighSurrogate(t(i)) AndAlso i + 1 < t.Length Then
                    cp = Char.ConvertToUtf32(t(i), t(i + 1))
                    len = 2
                Else
                    cp = AscW(t(i))
                End If
                Dim cw = CharWidth(cp)
                If w + cw > maxWidth - 1 Then Exit While
                sb.Append(t, i, len)
                w += cw
                i += len
            End While
            sb.Append("…")
            If _colors Then sb.Append(E & "[0m")
            Return sb.ToString()
        End Function

        ''' <summary>
        ''' Word-wraps styled text to a width. The first line gets firstPrefix, the rest restPrefix (both counted in the width).
        ''' Styles are carried over line breaks.
        ''' </summary>
        Public Shared Function WrapText(t As String, maxWidth As Integer, Optional firstPrefix As String = "", Optional restPrefix As String = "") As List(Of String)
            Dim lines As New List(Of String)
            Dim avail1 = Math.Max(8, maxWidth - Width(firstPrefix))
            Dim availN = Math.Max(8, maxWidth - Width(restPrefix))
            ' Split into tokens: words with their trailing spaces; ANSI sequences stay attached.
            Dim tokens As New List(Of String)
            Dim cur As New StringBuilder()
            Dim i = 0
            While i < t.Length
                If t(i) = ChrW(27) Then
                    Dim m = AnsiPattern.Match(t, i)
                    If m.Success AndAlso m.Index = i Then
                        cur.Append(m.Value)
                        i += m.Length
                        Continue While
                    End If
                End If
                cur.Append(t(i))
                If t(i) = " "c AndAlso (i + 1 >= t.Length OrElse t(i + 1) <> " "c) Then
                    tokens.Add(cur.ToString())
                    cur.Clear()
                End If
                i += 1
            End While
            If cur.Length > 0 Then tokens.Add(cur.ToString())

            Dim line As New StringBuilder()
            Dim lineW = 0
            Dim active As New StringBuilder() ' style codes in effect, replayed on the next line
            Dim avail = avail1
            Dim flush = Sub()
                            lines.Add(line.ToString().TrimEnd(" "c))
                            line.Clear()
                            lineW = 0
                            avail = availN
                            If active.Length > 0 Then line.Append(active.ToString())
                        End Sub
            For Each tok In tokens
                Dim tw = Width(tok.TrimEnd(" "c))
                If lineW > 0 AndAlso lineW + tw > avail Then flush()
                ' A single word longer than the line: break it hard.
                If tw > avail Then
                    For Each ch In SplitByWidth(tok, avail - lineW, avail)
                        If lineW > 0 AndAlso Width(ch) + lineW > avail Then flush()
                        line.Append(ch)
                        lineW += Width(ch)
                        TrackStyles(ch, active)
                    Next
                    Continue For
                End If
                line.Append(tok)
                lineW += Width(tok)
                TrackStyles(tok, active)
            Next
            If line.Length > 0 OrElse lines.Count = 0 Then lines.Add(line.ToString().TrimEnd(" "c))
            For n = 0 To lines.Count - 1
                lines(n) = If(n = 0, firstPrefix, restPrefix) & lines(n) & If(_colors AndAlso active.Length > 0 AndAlso n < lines.Count - 1, E & "[0m", "")
            Next
            Return lines
        End Function

        Private Shared Function SplitByWidth(tok As String, firstWidth As Integer, width As Integer) As List(Of String)
            Dim parts As New List(Of String)
            Dim sb As New StringBuilder()
            Dim w = 0
            Dim limit = Math.Max(1, firstWidth)
            Dim i = 0
            While i < tok.Length
                If tok(i) = ChrW(27) Then
                    Dim m = AnsiPattern.Match(tok, i)
                    If m.Success AndAlso m.Index = i Then
                        sb.Append(m.Value)
                        i += m.Length
                        Continue While
                    End If
                End If
                Dim cw = CharWidth(AscW(tok(i)))
                If w + cw > limit AndAlso w > 0 Then
                    parts.Add(sb.ToString())
                    sb.Clear()
                    w = 0
                    limit = Math.Max(1, width)
                End If
                sb.Append(tok(i))
                w += cw
                i += 1
            End While
            If sb.Length > 0 Then parts.Add(sb.ToString())
            Return parts
        End Function

        Private Shared Sub TrackStyles(tok As String, active As StringBuilder)
            For Each m As Match In AnsiPattern.Matches(tok)
                If m.Value.EndsWith("m") Then
                    If m.Value = E & "[0m" Then active.Clear() Else active.Append(m.Value)
                End If
            Next
            ' Too many tracked codes: keep the tail (later codes win anyway).
            If active.Length > 200 Then active.Remove(0, active.Length - 200)
        End Sub

        Public Shared Function Pad(t As String, w As Integer) As String
            Dim d = w - Width(t)
            Return If(d > 0, t & New String(" "c, d), t)
        End Function

    End Class

End Namespace
