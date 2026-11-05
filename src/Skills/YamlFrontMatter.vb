Option Strict On
Option Explicit On
Option Infer On

Namespace Skills

    ''' <summary>
    ''' Minimal YAML front matter reader for SKILL.md and slash-command files (Claude Code format): "key: value" pairs,
    ''' quoted values, "- item" lists, inline [a, b] lists and block scalars (| and >).
    ''' </summary>
    Friend NotInheritable Class YamlFrontMatter

        Public Shared Function Parse(content As String) As Dictionary(Of String, Object)
            Return Split(content).Front
        End Function

        ''' <summary>The front matter and the Markdown body after it.</summary>
        Public Shared Function Split(content As String) As (Front As Dictionary(Of String, Object), Body As String)
            Dim result As New Dictionary(Of String, Object)(StringComparer.OrdinalIgnoreCase)
            If content Is Nothing Then Return (result, "")
            Dim lines = content.Replace(vbCrLf, vbLf).Replace(vbCr, vbLf).Split(ControlChars.Lf)
            Dim i = 0
            While i < lines.Length AndAlso String.IsNullOrWhiteSpace(lines(i))
                i += 1
            End While
            If i >= lines.Length OrElse lines(i).Trim() <> "---" Then Return (result, content.Trim())
            i += 1
            Dim currentKey As String = Nothing
            While i < lines.Length
                Dim raw = lines(i)
                If raw.Trim() = "---" Then
                    i += 1
                    Exit While
                End If
                Dim trimmed = raw.TrimStart()
                If trimmed.StartsWith("- ", StringComparison.Ordinal) AndAlso currentKey IsNot Nothing Then
                    Dim existing As Object = Nothing
                    If Not result.TryGetValue(currentKey, existing) OrElse TypeOf existing IsNot List(Of String) Then
                        Dim list As New List(Of String)
                        If existing IsNot Nothing AndAlso Not String.IsNullOrWhiteSpace(existing.ToString()) Then list.Add(existing.ToString())
                        result(currentKey) = list
                    End If
                    DirectCast(result(currentKey), List(Of String)).Add(Unquote(trimmed.Substring(2).Trim()))
                ElseIf raw.Length > 0 AndAlso Not Char.IsWhiteSpace(raw(0)) AndAlso raw.IndexOf(":"c) > 0 Then
                    Dim sep = raw.IndexOf(":"c)
                    Dim key = raw.Substring(0, sep).Trim()
                    Dim value = raw.Substring(sep + 1).Trim()
                    currentKey = key
                    If value = "|" OrElse value = ">" OrElse value = "|-" OrElse value = ">-" Then
                        ' Block scalar: the indented lines that follow.
                        Dim folded = value.StartsWith(">")
                        Dim parts As New List(Of String)
                        While i + 1 < lines.Length AndAlso (lines(i + 1).Length = 0 OrElse Char.IsWhiteSpace(lines(i + 1)(0))) AndAlso lines(i + 1).Trim() <> "---"
                            i += 1
                            parts.Add(lines(i).Trim())
                        End While
                        result(key) = String.Join(If(folded, " ", vbLf), parts).Trim()
                    ElseIf value.StartsWith("[") AndAlso value.EndsWith("]") Then
                        result(key) = value.Substring(1, value.Length - 2).Split(","c).Select(Function(x) Unquote(x.Trim())).Where(Function(x) x.Length > 0).ToList()
                    Else
                        result(key) = Unquote(value)
                    End If
                End If
                i += 1
            End While
            Dim body = String.Join(vbLf, lines.Skip(i)).Trim()
            Return (result, body)
        End Function

        Private Shared Function Unquote(value As String) As String
            If value.Length >= 2 AndAlso ((value.StartsWith("""") AndAlso value.EndsWith("""")) OrElse (value.StartsWith("'") AndAlso value.EndsWith("'"))) Then
                Return value.Substring(1, value.Length - 2)
            End If
            Return value
        End Function

    End Class

End Namespace
