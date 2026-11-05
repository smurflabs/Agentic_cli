Option Strict On
Option Explicit On
Option Infer On

Imports System.Text
Imports System.Text.RegularExpressions

Namespace Ui

    ''' <summary>
    ''' Renders Markdown for the terminal as it streams in, a line at a time: headings, lists, quotes, rules, tables (aligned
    ''' when the table is complete), fenced code with syntax colours, and inline bold, italics, code, links and strikethrough.
    ''' The first line of an answer starts with ●, the rest are indented, and long lines wrap with the indent.
    ''' </summary>
    Friend NotInheritable Class MarkdownStream

        Private ReadOnly _emit As Action(Of String)
        Private ReadOnly _pending As New StringBuilder()
        Private ReadOnly _table As New List(Of String)
        Private _fence As String
        Private _fenceLang As String
        Private _started As Boolean
        Private _blankRun As Integer
        Public Property FirstPrefix As String
        Public Property Indent As String = "  "
        Public Property Muted As Boolean
        ''' <summary>Wrap width (0 = the terminal's).</summary>
        Public Property MaxWidth As Integer
        ''' <summary>Text was written since the last Reset.</summary>
        Public ReadOnly Property Started As Boolean
            Get
                Return _started
            End Get
        End Property

        Public Sub New(emit As Action(Of String), Optional firstPrefix As String = Nothing)
            _emit = emit
            Me.FirstPrefix = If(firstPrefix, Ansi.Text(Ansi.Dot) & " ")
        End Sub

        Public Sub Append(text As String)
            If String.IsNullOrEmpty(text) Then Return
            _pending.Append(text.Replace(ChrW(13), ""))
            While True
                Dim s = _pending.ToString()
                Dim nl = s.IndexOf(ChrW(10))
                If nl < 0 Then Exit While
                _pending.Remove(0, nl + 1)
                RenderLine(s.Substring(0, nl))
            End While
        End Sub

        ''' <summary>Renders what is left (a last line without a newline, an open table).</summary>
        Public Sub Finish()
            If _pending.Length > 0 Then
                Dim rest = _pending.ToString()
                _pending.Clear()
                RenderLine(rest)
            End If
            FlushTable()
            _fence = Nothing
        End Sub

        ''' <summary>The next text starts a new block (with its own ● prefix).</summary>
        Public Sub Reset()
            Finish()
            _started = False
            _blankRun = 0
        End Sub

        Private Sub Emit(styled As String, Optional wrap As Boolean = True)
            Dim prefix = If(_started, Indent, FirstPrefix)
            _started = True
            If Muted Then styled = Ansi.Muted(Ansi.Strip(styled))
            If Not wrap Then
                _emit(prefix & styled & Environment.NewLine)
                Return
            End If
            Dim sb As New StringBuilder()
            For Each l In Ansi.WrapText(styled, WrapWidth(), prefix, Indent)
                sb.Append(l).Append(Environment.NewLine)
            Next
            _emit(sb.ToString())
        End Sub

        Private Function WrapWidth() As Integer
            Return If(MaxWidth > 0, MaxWidth, Ansi.TermWidth() - 1)
        End Function

        Private Sub RenderLine(line As String)
            ' Inside a fenced code block.
            If _fence IsNot Nothing Then
                If line.TrimStart().StartsWith(_fence, StringComparison.Ordinal) AndAlso line.Trim().Trim(_fence(0)).Length = 0 Then
                    _fence = Nothing
                    Return
                End If
                Emit(Ansi.Dim2("│ ") & Highlighter.Line(line, _fenceLang), False)
                Return
            End If
            Dim trimmed = line.Trim()
            ' Tables: buffered until the table ends.
            If trimmed.StartsWith("|", StringComparison.Ordinal) AndAlso trimmed.Length > 1 Then
                _table.Add(trimmed)
                Return
            End If
            FlushTable()
            If trimmed.Length = 0 Then
                _blankRun += 1
                If _started AndAlso _blankRun = 1 Then _emit(Environment.NewLine)
                Return
            End If
            _blankRun = 0
            Dim fence = Regex.Match(trimmed, "^(`{3,}|~{3,})\s*([\w#+.-]*)")
            If fence.Success Then
                _fence = fence.Groups(1).Value
                _fenceLang = fence.Groups(2).Value.ToLowerInvariant()
                If _fenceLang.Length > 0 Then Emit(Ansi.Dim2("┌ " & _fenceLang), False)
                Return
            End If
            Dim h = Regex.Match(line, "^\s{0,3}(#{1,6})\s+(.*?)\s*#*\s*$")
            If h.Success Then
                Dim level = h.Groups(1).Value.Length
                Dim body = Inline(h.Groups(2).Value)
                Emit(If(level <= 2, Ansi.Bold(Ansi.Underline(Ansi.Accent(Ansi.Strip(body)))), Ansi.Bold(body)))
                Return
            End If
            If Regex.IsMatch(trimmed, "^([-*_])(\s*\1){2,}$") Then
                Emit(Ansi.Dim2(New String("─"c, Math.Min(60, Ansi.TermWidth() - 4))), False)
                Return
            End If
            Dim quote = Regex.Match(line, "^\s*>\s?(.*)$")
            If quote.Success Then
                Emit(Ansi.Dim2("▎ ") & Ansi.Italic(Ansi.Muted(Inline(quote.Groups(1).Value))))
                Return
            End If
            Dim bullet = Regex.Match(line, "^(\s*)([-*+])\s+(\[[ xX]\]\s+)?(.*)$")
            If bullet.Success Then
                Dim depth = bullet.Groups(1).Value.Replace(vbTab, "  ").Length \ 2
                Dim box = bullet.Groups(3).Value.Trim()
                Dim mark = If(box.Length > 0, If(box.ToLowerInvariant() = "[x]", Ansi.Ok(Ansi.CheckOn), Ansi.CheckOff), If(depth Mod 2 = 0, "•", "◦"))
                EmitIndented(New String(" "c, depth * 2) & mark & " ", Inline(bullet.Groups(4).Value))
                Return
            End If
            Dim numbered = Regex.Match(line, "^(\s*)(\d{1,3}[.)])\s+(.*)$")
            If numbered.Success Then
                Dim depth = numbered.Groups(1).Value.Length \ 2
                EmitIndented(New String(" "c, depth * 2) & numbered.Groups(2).Value & " ", Inline(numbered.Groups(3).Value))
                Return
            End If
            Emit(Inline(line.TrimEnd()))
        End Sub

        ''' <summary>A list item: continuation lines line up under the item's text.</summary>
        Private Sub EmitIndented(marker As String, styled As String)
            Dim prefix = If(_started, Indent, FirstPrefix)
            _started = True
            If Muted Then styled = Ansi.Muted(Ansi.Strip(styled))
            Dim sb As New StringBuilder()
            Dim hang = New String(" "c, Ansi.Width(Indent) + Ansi.Width(marker))
            For Each l In Ansi.WrapText(styled, WrapWidth(), prefix & marker, hang)
                sb.Append(l).Append(Environment.NewLine)
            Next
            _emit(sb.ToString())
        End Sub

        Private Sub FlushTable()
            If _table.Count = 0 Then Return
            Dim rows = _table.Select(Function(r) SplitRow(r)).ToList()
            _table.Clear()
            ' The |---|:--:| separator row only sets alignment.
            Dim aligns As New List(Of Char)
            Dim sepIndex = rows.FindIndex(Function(r) r.Count > 0 AndAlso r.All(Function(c) Regex.IsMatch(c.Trim(), "^:?-{2,}:?$")))
            If sepIndex >= 0 Then
                aligns = rows(sepIndex).Select(Function(c) If(c.Trim().EndsWith(":") AndAlso c.Trim().StartsWith(":"), "c"c, If(c.Trim().EndsWith(":"), "r"c, "l"c))).ToList()
                rows.RemoveAt(sepIndex)
            End If
            Dim cols = rows.Max(Function(r) r.Count)
            Dim cells = rows.Select(Function(r) Enumerable.Range(0, cols).Select(Function(i) If(i < r.Count, Inline(r(i).Trim()), "")).ToList()).ToList()
            Dim widths = Enumerable.Range(0, cols).Select(Function(i) cells.Max(Function(r) Ansi.Width(r(i)))).ToList()
            ' Too wide for the terminal: shrink the widest columns.
            Dim maxTotal = Ansi.TermWidth() - 6 - cols * 3
            While widths.Sum() > maxTotal AndAlso widths.Max() > 8
                Dim i = widths.IndexOf(widths.Max())
                widths(i) -= 1
            End While
            Dim b = Ansi.Dim2("│")
            Dim lineOf = Function(l As String, m As String, r As String) Ansi.Dim2(l & String.Join(m, widths.Select(Function(w) New String("─"c, w + 2))) & r)
            Emit(lineOf("┌", "┬", "┐"), False)
            For ri = 0 To cells.Count - 1
                Dim parts As New List(Of String)
                For ci = 0 To cols - 1
                    Dim v = Ansi.Truncate(cells(ri)(ci), widths(ci))
                    If ri = 0 AndAlso sepIndex = 1 Then v = Ansi.Bold(v)
                    Dim pad = widths(ci) - Ansi.Width(v)
                    Dim a = If(ci < aligns.Count, aligns(ci), "l"c)
                    Dim cell = If(a = "r"c, New String(" "c, pad) & v, If(a = "c"c, New String(" "c, pad \ 2) & v & New String(" "c, pad - pad \ 2), v & New String(" "c, pad)))
                    parts.Add(" " & cell & " ")
                Next
                Emit(b & String.Join(b, parts) & b, False)
                If ri = 0 AndAlso sepIndex = 1 AndAlso cells.Count > 1 Then Emit(lineOf("├", "┼", "┤"), False)
            Next
            Emit(lineOf("└", "┴", "┘"), False)
        End Sub

        Private Shared Function SplitRow(row As String) As List(Of String)
            Dim r = row.Trim()
            If r.StartsWith("|") Then r = r.Substring(1)
            If r.EndsWith("|") Then r = r.Substring(0, r.Length - 1)
            ' Split on | that is not escaped.
            Return Regex.Split(r, "(?<!\\)\|").Select(Function(c) c.Replace("\|", "|")).ToList()
        End Function

        ''' <summary>Inline Markdown to terminal styles.</summary>
        Public Shared Function Inline(text As String) As String
            If String.IsNullOrEmpty(text) Then Return ""
            ' Code spans first, so nothing inside them is styled.
            Dim codes As New List(Of String)
            Dim s = Regex.Replace(text, "(`+)(.+?)\1", Function(m)
                                                           codes.Add(Ansi.CodeColor(m.Groups(2).Value))
                                                           Return ChrW(1) & (codes.Count - 1) & ChrW(2)
                                                       End Function)
            s = Regex.Replace(s, "!?\[([^\]]+)\]\(([^)\s]+)(?:\s+""[^""]*"")?\)", Function(m) Ansi.Underline(Ansi.Info(m.Groups(1).Value)) & If(m.Groups(1).Value = m.Groups(2).Value, "", Ansi.Dim2(" (" & m.Groups(2).Value & ")")))
            s = Regex.Replace(s, "\*\*\*(.+?)\*\*\*", Function(m) Ansi.Bold(Ansi.Italic(m.Groups(1).Value)))
            s = Regex.Replace(s, "\*\*(.+?)\*\*", Function(m) Ansi.Bold(m.Groups(1).Value))
            s = Regex.Replace(s, "(?<![\w])__(.+?)__(?![\w])", Function(m) Ansi.Bold(m.Groups(1).Value))
            s = Regex.Replace(s, "(?<![\w*])\*(?!\s)(.+?)(?<!\s)\*(?![\w*])", Function(m) Ansi.Italic(m.Groups(1).Value))
            s = Regex.Replace(s, "(?<![\w])_(?!\s)(.+?)(?<!\s)_(?![\w])", Function(m) Ansi.Italic(m.Groups(1).Value))
            s = Regex.Replace(s, "~~(.+?)~~", Function(m) Ansi.Strike(m.Groups(1).Value))
            s = Regex.Replace(s, "<(https?://[^>\s]+)>", Function(m) Ansi.Underline(Ansi.Info(m.Groups(1).Value)))
            s = Regex.Replace(s, ChrW(1) & "(\d+)" & ChrW(2), Function(m) codes(Integer.Parse(m.Groups(1).Value)))
            Return s
        End Function

        ''' <summary>Renders a whole Markdown text at once (plans, skill bodies, exports).</summary>
        Public Shared Function Render(markdown As String, Optional firstPrefix As String = "  ", Optional indent As String = "  ", Optional width As Integer = 0) As String
            Dim sb As New StringBuilder()
            Dim r As New MarkdownStream(Sub(t) sb.Append(t), firstPrefix) With {.Indent = indent, .MaxWidth = width}
            r.Append(markdown)
            r.Finish()
            Return sb.ToString()
        End Function

    End Class

    ''' <summary>Light syntax colouring for code blocks and diffs: comments, strings, numbers, keywords, types and calls.</summary>
    Friend NotInheritable Class Highlighter

        Private Shared ReadOnly Keywords As New Dictionary(Of String, HashSet(Of String))(StringComparer.OrdinalIgnoreCase) From {
            {"js", Words("await async break case catch class const continue default delete do else export extends finally for from function if import in instanceof let new of return static super switch this throw try typeof var void while yield null true false undefined interface type enum implements public private protected readonly as")},
            {"py", Words("and as assert async await break class continue def del elif else except False finally for from global if import in is lambda None nonlocal not or pass raise return True try while with yield self")},
            {"cs", Words("abstract as async await base bool break byte case catch char class const continue decimal default delegate do double else enum event explicit extern false finally fixed float for foreach goto if implicit in int interface internal is lock long namespace new null object operator out override params private protected public readonly record ref return sealed short static string struct switch this throw true try typeof uint ulong using var virtual void volatile while get set init")},
            {"vb", Words("addhandler andalso as async await boolean byref byval case catch class const dim do double each else elseif end enum exit false finally for friend function get handles if implements imports in inherits integer interface is isnot let long loop me module mustinherit mustoverride namespace new next not nothing notinheritable of option optional or orelse overrides partial private property protected public readonly return select set shared single static string structure sub then throw to true try typeof using when while with")},
            {"sh", Words("if then else elif fi for while do done case esac function in return export local echo cd ls sudo git npm node python pip dotnet")},
            {"ps", Words("function param begin process end if else elseif foreach for while do switch try catch finally return throw break continue in")},
            {"sql", Words("select from where join inner left right outer on group by order having insert into values update set delete create table alter drop index as and or not null is in like limit offset distinct count sum avg min max case when then else end union all primary key")},
            {"go", Words("break case chan const continue default defer else fallthrough for func go goto if import interface map package range return select struct switch type var nil true false")},
            {"rs", Words("as async await break const continue crate else enum extern false fn for if impl in let loop match mod move mut pub ref return self static struct super trait true type unsafe use where while")},
            {"java", Words("abstract boolean break byte case catch char class const continue default do double else enum extends final finally float for if implements import instanceof int interface long new null package private protected public return short static super switch this throw throws true false try void while var")}}
        Private Shared ReadOnly LangAlias As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase) From {
            {"javascript", "js"}, {"jsx", "js"}, {"ts", "js"}, {"typescript", "js"}, {"tsx", "js"}, {"node", "js"}, {"json", "js"}, {"mjs", "js"}, {"cjs", "js"},
            {"python", "py"}, {"csharp", "cs"}, {"c#", "cs"}, {"vbnet", "vb"}, {"vb.net", "vb"}, {"bash", "sh"}, {"shell", "sh"}, {"zsh", "sh"}, {"console", "sh"},
            {"powershell", "ps"}, {"ps1", "ps"}, {"pwsh", "ps"}, {"golang", "go"}, {"rust", "rs"}, {"c", "java"}, {"cpp", "java"}, {"c++", "java"}, {"h", "java"},
            {"kotlin", "java"}, {"swift", "java"}, {"php", "js"}, {"ruby", "py"}, {"rb", "py"}, {"yaml", "py"}, {"yml", "py"}, {"toml", "py"}, {"cmd", "sh"}, {"bat", "sh"}}

        Private Shared Function Words(s As String) As HashSet(Of String)
            Return New HashSet(Of String)(s.Split(" "c), StringComparer.Ordinal)
        End Function

        ''' <summary>The language for a file name's extension (for diffs and file previews).</summary>
        Public Shared Function LangOf(fileName As String) As String
            Dim ext = IO.Path.GetExtension(If(fileName, "")).TrimStart("."c).ToLowerInvariant()
            If ext = "" Then Return ""
            If Keywords.ContainsKey(ext) Then Return ext
            Dim a As String = Nothing
            If LangAlias.TryGetValue(ext, a) Then Return a
            Select Case ext
                Case "html", "htm", "xml", "svg", "xaml", "vbproj", "csproj", "props", "targets", "config" : Return "html"
                Case "md", "markdown" : Return "md"
                Case Else : Return ext
            End Select
        End Function

        Private Shared ReadOnly TokenPattern As New Regex(
            "(?<c>//.*$|/\*.*?\*/|#(?![\w{]).*$|--\s.*$)|(?<s>""(?:[^""\\]|\\.)*""|'(?:[^'\\]|\\.)*'|`(?:[^`\\]|\\.)*`)|(?<n>\b\d[\d_.]*(?:e[+-]?\d+)?[a-z]*\b|0x[\da-f]+)|(?<w>[A-Za-z_$][\w$]*)",
            RegexOptions.Compiled Or RegexOptions.IgnoreCase)
        Private Shared ReadOnly VbTokenPattern As New Regex(
            "(?<c>'.*$|\bREM\b.*$)|(?<s>""(?:[^""]|"""")*"")|(?<n>\b\d[\d_.]*\b|&H[\dA-F]+)|(?<w>[A-Za-z_][\w]*)",
            RegexOptions.Compiled Or RegexOptions.IgnoreCase)
        Private Shared ReadOnly HtmlPattern As New Regex("(?<c><!--.*?-->)|(?<t></?[\w:-]+)|(?<a>[\w:-]+=)(?<s>""[^""]*""|'[^']*')?", RegexOptions.Compiled)

        Public Shared Function Line(code As String, lang As String) As String
            If Not Ansi.Colors OrElse String.IsNullOrEmpty(code) Then Return code
            Dim l = If(lang, "").ToLowerInvariant()
            Dim a As String = Nothing
            If LangAlias.TryGetValue(l, a) Then l = a
            If l = "html" OrElse l = "xml" OrElse l = "svg" Then
                Return HtmlPattern.Replace(code, Function(m)
                                                      If m.Groups("c").Success Then Return Ansi.Dim2(m.Value)
                                                      If m.Groups("t").Success Then Return Ansi.Fg(204, m.Value)
                                                      Return Ansi.Fg(150, m.Groups("a").Value) & If(m.Groups("s").Success, Ansi.Fg(180, m.Groups("s").Value), "")
                                                  End Function)
            End If
            If l = "md" OrElse l = "text" OrElse l = "txt" OrElse l = "diff" Then Return Ansi.Text(code)
            Dim kws As HashSet(Of String) = Nothing
            If Not Keywords.TryGetValue(l, kws) Then kws = Keywords("js")
            Dim pattern = If(l = "vb", VbTokenPattern, TokenPattern)
            Dim caseless = l = "vb" OrElse l = "sql" OrElse l = "ps"
            Return pattern.Replace(code, Function(m)
                                             If m.Groups("c").Success Then
                                                 ' '#' starts a comment only in shell/python-like languages.
                                                 If m.Value.StartsWith("#") AndAlso Not (l = "py" OrElse l = "sh" OrElse l = "ps" OrElse l = "yaml") Then Return m.Value
                                                 If m.Value.StartsWith("--") AndAlso l <> "sql" Then Return m.Value
                                                 Return Ansi.Fg(244, m.Value)
                                             End If
                                             If m.Groups("s").Success Then Return Ansi.Fg(180, m.Value)
                                             If m.Groups("n").Success Then Return Ansi.Fg(151, m.Value)
                                             Dim w = m.Value
                                             If kws.Contains(If(caseless, w.ToLowerInvariant(), w)) Then Return Ansi.Fg(176, w)
                                             Dim nextIndex = m.Index + m.Length
                                             If nextIndex < code.Length AndAlso code(nextIndex) = "("c Then Return Ansi.Fg(222, w)
                                             If Char.IsUpper(w(0)) Then Return Ansi.Fg(80, w)
                                             Return Ansi.Text(w)
                                         End Function)
        End Function

    End Class

End Namespace
