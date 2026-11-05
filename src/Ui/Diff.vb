Option Strict On
Option Explicit On
Option Infer On

Imports System.Text

Namespace Ui

    Friend Enum DiffKind
        Same
        Removed
        Added
    End Enum

    Friend Structure DiffLine
        Public Kind As DiffKind
        Public Text As String
        Public OldNo As Integer
        Public NewNo As Integer
    End Structure

    ''' <summary>Line diffs (Myers' algorithm) and their display: numbered, red for removed lines, green for added ones.</summary>
    Friend NotInheritable Class Diff

        Public Shared Function SplitLines(text As String) As String()
            If String.IsNullOrEmpty(text) Then Return Array.Empty(Of String)()
            Dim t = text.Replace(ChrW(13) & ChrW(10), ChrW(10)).Replace(ChrW(13), ChrW(10))
            If t.EndsWith(ChrW(10)) Then t = t.Substring(0, t.Length - 1)
            Return t.Split(ChrW(10))
        End Function

        ''' <summary>Every line of both texts in order, marked same / removed / added.</summary>
        Public Shared Function Compute(oldText As String, newText As String) As List(Of DiffLine)
            Dim a = SplitLines(oldText)
            Dim b = SplitLines(newText)
            Dim result As New List(Of DiffLine)
            ' Common start and end are not part of the search.
            Dim pre = 0
            While pre < a.Length AndAlso pre < b.Length AndAlso a(pre) = b(pre)
                pre += 1
            End While
            Dim suf = 0
            While suf < a.Length - pre AndAlso suf < b.Length - pre AndAlso a(a.Length - 1 - suf) = b(b.Length - 1 - suf)
                suf += 1
            End While
            For i = 0 To pre - 1
                result.Add(New DiffLine With {.Kind = DiffKind.Same, .Text = a(i), .OldNo = i + 1, .NewNo = i + 1})
            Next
            Dim midA = a.Skip(pre).Take(a.Length - pre - suf).ToArray()
            Dim midB = b.Skip(pre).Take(b.Length - pre - suf).ToArray()
            For Each op In Myers(midA, midB)
                Dim d As New DiffLine With {.Kind = op.Kind, .Text = If(op.Kind = DiffKind.Added, midB(op.Index), midA(op.Index))}
                If op.Kind <> DiffKind.Added Then d.OldNo = pre + op.Index + 1
                If op.Kind = DiffKind.Same Then d.NewNo = pre + op.OtherIndex + 1
                If op.Kind = DiffKind.Added Then d.NewNo = pre + op.Index + 1
                result.Add(d)
            Next
            For i = 0 To suf - 1
                Dim ai = a.Length - suf + i
                Dim bi = b.Length - suf + i
                result.Add(New DiffLine With {.Kind = DiffKind.Same, .Text = a(ai), .OldNo = ai + 1, .NewNo = bi + 1})
            Next
            Return result
        End Function

        Private Structure Op
            Public Kind As DiffKind
            Public Index As Integer
            Public OtherIndex As Integer
        End Structure

        ''' <summary>Myers O((N+M)D); falls back to delete-all/add-all for very large, very different inputs.</summary>
        Private Shared Function Myers(a As String(), b As String()) As List(Of Op)
            Dim n = a.Length, m = b.Length
            Dim ops As New List(Of Op)
            If n = 0 AndAlso m = 0 Then Return ops
            Dim max = n + m
            If CLng(n) * m > 25_000_000L Then
                For i = 0 To n - 1 : ops.Add(New Op With {.Kind = DiffKind.Removed, .Index = i}) : Next
                For j = 0 To m - 1 : ops.Add(New Op With {.Kind = DiffKind.Added, .Index = j}) : Next
                Return ops
            End If
            Dim v(2 * max + 1) As Integer
            Dim trace As New List(Of Integer())
            Dim found = False
            For d = 0 To max
                trace.Add(DirectCast(v.Clone(), Integer()))
                For k = -d To d Step 2
                    Dim x As Integer
                    If k = -d OrElse (k <> d AndAlso v(max + k - 1) < v(max + k + 1)) Then
                        x = v(max + k + 1)
                    Else
                        x = v(max + k - 1) + 1
                    End If
                    Dim y = x - k
                    While x < n AndAlso y < m AndAlso a(x) = b(y)
                        x += 1
                        y += 1
                    End While
                    v(max + k) = x
                    If x >= n AndAlso y >= m Then
                        found = True
                        Exit For
                    End If
                Next
                If found Then Exit For
            Next
            ' Walk back through the trace.
            Dim cx = n, cy = m
            Dim rev As New List(Of Op)
            For d = trace.Count - 1 To 0 Step -1
                Dim vv = trace(d)
                Dim k = cx - cy
                Dim prevK As Integer
                If k = -d OrElse (k <> d AndAlso vv(max + k - 1) < vv(max + k + 1)) Then prevK = k + 1 Else prevK = k - 1
                Dim prevX = vv(max + prevK)
                Dim prevY = prevX - prevK
                While cx > prevX AndAlso cy > prevY
                    cx -= 1
                    cy -= 1
                    rev.Add(New Op With {.Kind = DiffKind.Same, .Index = cx, .OtherIndex = cy})
                End While
                If d > 0 Then
                    If cx = prevX Then
                        cy -= 1
                        rev.Add(New Op With {.Kind = DiffKind.Added, .Index = cy})
                    Else
                        cx -= 1
                        rev.Add(New Op With {.Kind = DiffKind.Removed, .Index = cx})
                    End If
                End If
            Next
            rev.Reverse()
            Return rev
        End Function

        Public Shared Function Counts(lines As List(Of DiffLine)) As (Added As Integer, Removed As Integer)
            Return (lines.Where(Function(l) l.Kind = DiffKind.Added).Count(), lines.Where(Function(l) l.Kind = DiffKind.Removed).Count())
        End Function

        ''' <summary>"with 3 additions and 1 removal" (Claude Code's wording).</summary>
        Public Shared Function Summary(lines As List(Of DiffLine)) As String
            Dim c = Counts(lines)
            Dim parts As New List(Of String)
            If c.Added > 0 Then parts.Add(Ansi.Bold(c.Added.ToString()) & If(c.Added = 1, " addition", " additions"))
            If c.Removed > 0 Then parts.Add(Ansi.Bold(c.Removed.ToString()) & If(c.Removed = 1, " removal", " removals"))
            Return If(parts.Count = 0, "with no changes", "with " & String.Join(" and ", parts))
        End Function

        ''' <summary>The changed parts with a few lines of context, numbered, coloured; at most maxLines lines.</summary>
        Public Shared Function Render(lines As List(Of DiffLine), fileName As String, Optional context As Integer = 3, Optional maxLines As Integer = 60, Optional indent As String = "      ") As String
            Dim show(lines.Count - 1) As Boolean
            For i = 0 To lines.Count - 1
                If lines(i).Kind <> DiffKind.Same Then
                    For j = Math.Max(0, i - context) To Math.Min(lines.Count - 1, i + context)
                        show(j) = True
                    Next
                End If
            Next
            Dim lang = Highlighter.LangOf(fileName)
            Dim numWidth = Math.Max(3, lines.Select(Function(l) Math.Max(l.OldNo, l.NewNo)).DefaultIfEmpty(0).Max().ToString().Length)
            Dim termW = Math.Min(Ansi.TermWidth(), 121) - Ansi.Width(indent) - numWidth - 9
            Dim sb As New StringBuilder()
            Dim written = 0
            Dim lastShown = -2
            For i = 0 To lines.Count - 1
                If Not show(i) Then Continue For
                If written >= maxLines Then
                    Dim rest = Enumerable.Range(i, lines.Count - i).Count(Function(x) show(x) AndAlso lines(x).Kind <> DiffKind.Same)
                    If rest > 0 Then sb.Append(indent).Append(Ansi.Muted($"… {rest} more changed lines")).Append(Environment.NewLine)
                    Exit For
                End If
                If lastShown >= 0 AndAlso i > lastShown + 1 Then sb.Append(indent).Append(Ansi.Dim2(New String(" "c, numWidth) & "  ⋮")).Append(Environment.NewLine)
                lastShown = i
                Dim l = lines(i)
                Dim no = If(l.Kind = DiffKind.Removed, l.OldNo, l.NewNo)
                Dim body = l.Text.Replace(vbTab, "    ")
                If Ansi.Width(body) > termW Then body = Ansi.Truncate(body, termW)
                Dim num = Ansi.Dim2(no.ToString().PadLeft(numWidth))
                Select Case l.Kind
                    Case DiffKind.Removed
                        sb.Append(indent).Append(num).Append(" ").Append(Ansi.Bg(52, Ansi.Fg(217, "-" & " " & Ansi.Pad(body, termW))))
                    Case DiffKind.Added
                        sb.Append(indent).Append(num).Append(" ").Append(Ansi.Bg(22, Ansi.Fg(194, "+" & " " & Ansi.Pad(body, termW))))
                    Case Else
                        sb.Append(indent).Append(num).Append("   ").Append(Highlighter.Line(body, lang))
                End Select
                sb.Append(Environment.NewLine)
                written += 1
            Next
            Return sb.ToString()
        End Function

    End Class

End Namespace
