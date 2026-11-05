Option Strict On
Option Explicit On
Option Infer On

Imports System.IO
Imports System.Text
Imports System.Text.Json
Imports System.Text.Json.Nodes
Imports AgenticCli.Agent
Imports AgenticCli.Core
Imports AgenticCli.Ui

Namespace Tools

    ''' <summary>A text file's content with its encoding, BOM and line endings, so a rewrite keeps them.</summary>
    Friend NotInheritable Class TextFile
        Public Property Text As String = ""
        Public Property Crlf As Boolean
        Public Property Bom As Boolean
        Public Property BomBytes As Byte() = Array.Empty(Of Byte)()
        Public Property Enc As Encoding = New UTF8Encoding(False)
        Public Property Exists As Boolean

        Public Shared Function Load(file As String) As TextFile
            Dim t As New TextFile()
            If Not IO.File.Exists(file) Then Return t
            t.Exists = True
            Dim bytes = IO.File.ReadAllBytes(file)
            Dim start = 0
            If bytes.Length >= 3 AndAlso bytes(0) = &HEF AndAlso bytes(1) = &HBB AndAlso bytes(2) = &HBF Then
                t.Bom = True : start = 3 : t.Enc = New UTF8Encoding(False) : t.BomBytes = {&HEF, &HBB, &HBF}
            ElseIf bytes.Length >= 2 AndAlso bytes(0) = &HFF AndAlso bytes(1) = &HFE Then
                t.Bom = True : start = 2 : t.Enc = New UnicodeEncoding(False, False) : t.BomBytes = {&HFF, &HFE}
            ElseIf bytes.Length >= 2 AndAlso bytes(0) = &HFE AndAlso bytes(1) = &HFF Then
                t.Bom = True : start = 2 : t.Enc = New UnicodeEncoding(True, False) : t.BomBytes = {&HFE, &HFF}
            End If
            Dim raw As String
            Try
                raw = If(t.Bom, t.Enc, New UTF8Encoding(False, True)).GetString(bytes, start, bytes.Length - start)
            Catch ex As DecoderFallbackException
                t.Enc = Encoding.Latin1
                raw = Encoding.Latin1.GetString(bytes, start, bytes.Length - start)
            End Try
            t.Crlf = raw.Contains(ChrW(13) & ChrW(10))
            t.Raw = raw
            t.Text = raw.Replace(ChrW(13) & ChrW(10), ChrW(10))
            Return t
        End Function

        ''' <summary>The file's text as it was, line endings included (what Save keeps them from).</summary>
        Public Property Raw As String = ""

        Public Sub Save(file As String, lfText As String)
            lfText = lfText.Replace(ChrW(13) & ChrW(10), ChrW(10))
            ' Each unchanged line keeps its own ending (a file with mixed endings is not rewritten); new lines take the usual one.
            Dim body = If(Crlf, LineEndings.Restore(Raw, lfText), lfText)
            Dim dir = Path.GetDirectoryName(file)
            If Not String.IsNullOrEmpty(dir) Then Directory.CreateDirectory(dir)
            Dim data = Enc.GetBytes(body)
            If BomBytes.Length > 0 Then data = BomBytes.Concat(data).ToArray()
            IO.File.WriteAllBytes(file, data)
        End Sub

        Public Shared Function IsBinary(file As String) As Boolean
            Try
                Using fs = IO.File.OpenRead(file)
                    Dim buf(8191) As Byte
                    Dim n = fs.Read(buf, 0, buf.Length)
                    If n >= 2 AndAlso ((buf(0) = &HFF AndAlso buf(1) = &HFE) OrElse (buf(0) = &HFE AndAlso buf(1) = &HFF)) Then Return False
                    For i = 0 To n - 1
                        If buf(i) = 0 Then Return True
                    Next
                End Using
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
            End Try
            Return False
        End Function
    End Class

    ''' <summary>
    ''' Line endings for a changed file: lines the change did not touch (the same lines at the start and at the end) keep the
    ''' ending they had, and the changed lines take the file's usual one. A file with mixed endings is then changed only where
    ''' it was edited, instead of every line.
    ''' </summary>
    Friend NotInheritable Class LineEndings

        Private Sub New()
        End Sub

        Public Shared Function Restore(originalRaw As String, newLf As String) As String
            Dim original = If(originalRaw, "")
            Dim crlf = 0, lf = 0
            Dim oldLines As New List(Of String), oldEnds As New List(Of String)
            Dim start = 0
            For i = 0 To original.Length - 1
                If original(i) <> ChrW(10) Then Continue For
                Dim isCrlf = i > start AndAlso original(i - 1) = ChrW(13)
                oldLines.Add(original.Substring(start, i - start - If(isCrlf, 1, 0)))
                oldEnds.Add(If(isCrlf, vbCrLf, vbLf))
                If isCrlf Then crlf += 1 Else lf += 1
                start = i + 1
            Next
            Dim usual = If(crlf > lf, vbCrLf, vbLf)
            Dim parts = If(newLf, "").Split(ChrW(10))
            Dim count = parts.Length - 1
            Dim ends(Math.Max(0, count) - 1) As String
            Dim p = 0
            While p < count AndAlso p < oldLines.Count AndAlso parts(p) = oldLines(p)
                ends(p) = oldEnds(p)
                p += 1
            End While
            Dim s = 0
            While s < count - p AndAlso s < oldLines.Count - p AndAlso parts(count - 1 - s) = oldLines(oldLines.Count - 1 - s)
                ends(count - 1 - s) = oldEnds(oldLines.Count - 1 - s)
                s += 1
            End While
            ' Changed lines replacing as many old ones keep the old ones' endings, one for one.
            Dim sameCount = count - p - s = oldLines.Count - p - s
            Dim sb As New StringBuilder()
            For i = 0 To count - 1
                sb.Append(parts(i)).Append(If(ends(i), If(sameCount, oldEnds(i), usual)))
            Next
            Return sb.Append(parts(count)).ToString()
        End Function

    End Class

    ''' <summary>Edits shared by Edit and MultiEdit (on text with LF line endings).</summary>
    Friend NotInheritable Class EditLogic

        Public Shared Function Apply(text As String, oldString As String, newString As String, replaceAll As Boolean) As (NewText As String, [Error] As String)
            oldString = If(oldString, "").Replace(ChrW(13) & ChrW(10), ChrW(10))
            newString = If(newString, "").Replace(ChrW(13) & ChrW(10), ChrW(10))
            If oldString = newString Then Return (Nothing, "No changes to make: old_string and new_string are exactly the same.")
            If oldString.Length = 0 Then
                If text.Length = 0 Then Return (newString, Nothing)
                Return (Nothing, "old_string is empty; use Write to replace a whole file, or give the text to replace.")
            End If
            Dim count = CountOf(text, oldString)
            If count = 0 Then Return (Nothing, "String to replace not found in file." & vbLf & "String: " & oldString)
            If count > 1 AndAlso Not replaceAll Then
                Return (Nothing, $"Found {count} matches of the string to replace, but replace_all is false. To replace all occurrences, set replace_all to true. To replace only one occurrence, please provide more context to uniquely identify the instance." & vbLf & "String: " & oldString)
            End If
            If replaceAll Then Return (text.Replace(oldString, newString), Nothing)
            Dim i = text.IndexOf(oldString, StringComparison.Ordinal)
            Return (text.Substring(0, i) & newString & text.Substring(i + oldString.Length), Nothing)
        End Function

        Public Shared Function CountOf(text As String, part As String) As Integer
            Dim n = 0, i = 0
            While True
                i = text.IndexOf(part, i, StringComparison.Ordinal)
                If i < 0 Then Exit While
                n += 1
                i += part.Length
            End While
            Return n
        End Function

        ''' <summary>"cat -n" lines around the first change, for the model to check its edit.</summary>
        Public Shared Function Snippet(newText As String, around As String, Optional context As Integer = 4) As String
            Dim idx = If(String.IsNullOrEmpty(around), -1, newText.IndexOf(around, StringComparison.Ordinal))
            Dim lines = newText.Split(ChrW(10))
            Dim startLine = If(idx < 0, 0, newText.Substring(0, idx).Count(Function(c) c = ChrW(10)))
            Dim endLine = startLine + If(around, "").Count(Function(c) c = ChrW(10))
            Dim a = Math.Max(0, startLine - context), b = Math.Min(lines.Length - 1, endLine + context)
            Dim sb As New StringBuilder()
            For i = a To b
                sb.Append((i + 1).ToString().PadLeft(6)).Append(vbTab).Append(lines(i)).Append(vbLf)
            Next
            Return sb.ToString()
        End Function

        ''' <summary>Refuses an edit of a file that was not read in this session, or that changed since it was read.</summary>
        Public Shared Function CheckFresh(state As AgentState, full As String) As String
            If Not File.Exists(full) Then Return Nothing
            Dim readAt As DateTime
            If Not state.ReadFiles.TryGetValue(full, readAt) Then Return "File has not been read yet. Read it first before writing to it."
            Dim readSize As Long
            Dim changed = File.GetLastWriteTimeUtc(full) <> readAt OrElse (state.ReadSizes.TryGetValue(full, readSize) AndAlso New FileInfo(full).Length <> readSize)
            If changed Then Return "File has been modified since read, either by the user or by a linter. Read it again before attempting to write it."
            Return Nothing
        End Function

        Public Shared Function DiffDetail(oldText As String, newText As String, file As String, Optional maxLines As Integer = 40) As (Summary As String, Detail As String)
            Dim lines = Diff.Compute(oldText, newText)
            Return (Diff.Summary(lines), Diff.Render(lines, file, 3, maxLines, "      "))
        End Function

    End Class

    Friend NotInheritable Class ReadTool
        Implements ITool

        Public ReadOnly Property Name As String = "Read" Implements ITool.Name
        Public ReadOnly Property Description As String = "Reads a file from the local filesystem. file_path may be absolute or relative to the working directory. " &
            "By default it reads up to 2000 lines from the start; for long files give offset (1-based line) and limit. Lines longer than 2000 characters are cut. " &
            "Output is in cat -n format (line numbers, a tab, the line). Jupyter notebooks (.ipynb) are shown cell by cell with their outputs. " &
            "Read a file before you Edit or Write it. Read several files you will need, one after the other, instead of guessing their contents. " &
            "Binary files (images, PDFs, archives) are described, not shown." Implements ITool.Description
        Public ReadOnly Property IsReadOnly As Boolean = True Implements ITool.IsReadOnly

        Public Function Parameters() As JsonObject Implements ITool.Parameters
            Return ToolKit.Schema({"file_path"}, ("file_path", "string", "The file to read"),
                                  ("offset", "integer", "The line to start at (1-based); only for files too long to read at once"),
                                  ("limit", "integer", "How many lines to read"))
        End Function

        Public Function Title(input As JsonElement, state As AgentState) As String Implements ITool.Title
            Return "Read(" & state.Display(state.Resolve(ToolKit.Arg(input, "file_path", ""))) & ")"
        End Function

        Public Function Preview(input As JsonElement, state As AgentState) As String Implements ITool.Preview
            Return Nothing
        End Function

        Public Function RunAsync(input As JsonElement, ctx As ToolContext) As Task(Of ToolResult) Implements ITool.RunAsync
            Return Task.FromResult(Run(input, ctx.State))
        End Function

        Private Function Run(input As JsonElement, state As AgentState) As ToolResult
            Dim p = ToolKit.Arg(input, "file_path")
            If String.IsNullOrWhiteSpace(p) Then Return ToolResult.Fail("file_path is required.")
            Dim full = state.Resolve(p)
            If Directory.Exists(full) Then Return ToolResult.Fail($"EISDIR: {full} is a directory. Use LS to list it.")
            If Not File.Exists(full) Then
                Dim hint = ""
                Try
                    Dim dir = Path.GetDirectoryName(full)
                    If Directory.Exists(dir) Then
                        Dim name = Path.GetFileNameWithoutExtension(full)
                        Dim similar = Directory.EnumerateFiles(dir).Select(Function(f) Path.GetFileName(f)).Where(Function(f) f.StartsWith(name.Substring(0, Math.Min(3, name.Length)), StringComparison.OrdinalIgnoreCase)).Take(5).ToList()
                        If similar.Count > 0 Then hint = " Did you mean: " & String.Join(", ", similar) & "?"
                    End If
                Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException OrElse TypeOf ex Is ArgumentException
                End Try
                Return ToolResult.Fail($"File does not exist: {full}.{hint} The current working directory is {state.Cwd}.", "File not found")
            End If
            Dim ext = Path.GetExtension(full).ToLowerInvariant()
            Dim size = New FileInfo(full).Length
            If ext = ".ipynb" Then Return ReadNotebook(full, state)
            ' A picture goes to the model as an image (one that cannot see pictures gets a description of it from the server).
            If {".png", ".jpg", ".jpeg", ".webp"}.Contains(ext) AndAlso size > 0 AndAlso size <= 8L * 1024 * 1024 Then
                state.MarkRead(full)
                Return New ToolResult With {.Ok = True, .Content = $"{full} is a picture ({size:N0} bytes); it follows as an image.",
                                            .Summary = $"Read image ({size / 1024.0:N0} KB)", .ImageMime = If(ext = ".png", "image/png", If(ext = ".webp", "image/webp", "image/jpeg")),
                                            .Image = File.ReadAllBytes(full), .ImageName = Path.GetFileName(full)}
            End If
            If {".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".ico", ".pdf", ".zip", ".exe", ".dll"}.Contains(ext) OrElse TextFile.IsBinary(full) Then
                state.MarkRead(full)
                Return ToolResult.Done($"{full} is a binary file ({ext.TrimStart("."c)}, {size:N0} bytes); its content cannot be shown as text." &
                                       If({".gif", ".bmp"}.Contains(ext), " Pictures are shown to you as PNG, JPEG or WebP: convert it, or the user can attach it with @path.", ""),
                                       $"Binary file ({size:N0} bytes)")
            End If
            If size > 50L * 1024 * 1024 Then Return ToolResult.Fail($"The file is {size / 1048576.0:N1} MB, too large to read. Use Grep to find the parts you need, or offset and limit.")
            Dim tf = TextFile.Load(full)
            state.MarkRead(full)
            If tf.Text.Length = 0 Then Return ToolResult.Done("<system-reminder>The file exists but its contents are empty.</system-reminder>", "Read 0 lines (empty file)")
            Dim lines = tf.Text.Split(ChrW(10))
            If lines.Length > 1 AndAlso lines(lines.Length - 1) = "" Then lines = lines.Take(lines.Length - 1).ToArray()
            Dim offset = Math.Max(1, ToolKit.ArgInt(input, "offset", 1))
            Dim limit = Math.Max(1, ToolKit.ArgInt(input, "limit", 2000))
            If offset > lines.Length Then Return ToolResult.Fail($"The file has {lines.Length} lines; offset {offset} is past the end.")
            Dim lastLine = Math.Min(lines.Length, offset + limit - 1)
            Dim sb As New StringBuilder()
            For i = offset - 1 To lastLine - 1
                Dim l = lines(i)
                If l.Length > 2000 Then l = l.Substring(0, 2000) & "… [line cut]"
                sb.Append((i + 1).ToString().PadLeft(6)).Append(ChrW(9)).Append(l).Append(ChrW(10))
            Next
            Dim count = lastLine - offset + 1
            If lastLine < lines.Length Then sb.Append($"{vbLf}(The file has {lines.Length} lines; this shows {offset}-{lastLine}. Use offset and limit to read more.)")
            Dim summary = $"Read {Ansi.Bold(count.ToString())} line{If(count = 1, "", "s")}" & If(count < lines.Length, Ansi.Muted($" ({offset}-{lastLine} of {lines.Length})"), "")
            Return ToolResult.Done(sb.ToString().TrimEnd(ChrW(10)), summary)
        End Function

        Private Shared Function ReadNotebook(full As String, state As AgentState) As ToolResult
            Try
                Dim nb = Jx.ParseObject(File.ReadAllText(full))
                Dim cells = TryCast(nb("cells"), JsonArray)
                If cells Is Nothing Then Return ToolResult.Fail("Not a Jupyter notebook (no cells).")
                Dim sb As New StringBuilder()
                Dim i = 0
                For Each c In cells.OfType(Of JsonObject)()
                    Dim id = Jx.NodeStr(c("id"), "cell-" & i)
                    sb.Append($"<cell id=""{id}"" number=""{i}"" type=""{Jx.NodeStr(c("cell_type"), "code")}"">").Append(vbLf)
                    sb.Append(SourceOf(c("source"))).Append(vbLf)
                    Dim outputs = TryCast(c("outputs"), JsonArray)
                    If outputs IsNot Nothing Then
                        For Each o In outputs.OfType(Of JsonObject)()
                            Dim text = If(SourceOf(o("text")), "")
                            Dim data = TryCast(o("data"), JsonObject)
                            If text.Length = 0 AndAlso data IsNot Nothing Then text = If(SourceOf(data("text/plain")), If(data.ContainsKey("image/png"), "[image output]", ""))
                            If text.Length = 0 AndAlso o.ContainsKey("ename") Then text = Jx.NodeStr(o("ename")) & ": " & Jx.NodeStr(o("evalue"))
                            If text.Length > 0 Then sb.Append("<output>").Append(vbLf).Append(ToolKit.Clip(text, 4000)).Append(vbLf).Append("</output>").Append(vbLf)
                        Next
                    End If
                    sb.Append("</cell>").Append(vbLf)
                    i += 1
                Next
                state.MarkRead(full)
                Return ToolResult.Done(ToolKit.Clip(sb.ToString(), 60000), $"Read {i} cells")
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is JsonException
                Return ToolResult.Fail("Could not read the notebook: " & ex.Message)
            End Try
        End Function

        Friend Shared Function SourceOf(n As JsonNode) As String
            If n Is Nothing Then Return Nothing
            Dim a = TryCast(n, JsonArray)
            If a IsNot Nothing Then Return String.Concat(a.Select(Function(x) Jx.NodeStr(x, "")))
            Return Jx.NodeStr(n, "")
        End Function

    End Class

    Friend NotInheritable Class WriteTool
        Implements ITool

        Public ReadOnly Property Name As String = "Write" Implements ITool.Name
        Public ReadOnly Property Description As String = "Writes a file (creating folders as needed), replacing it when it exists. " &
            "For an existing file you must Read it first, and prefer Edit, which changes only what is needed. " &
            "Do not create documentation files (*.md, README) unless the user asks. Line endings and encoding of an existing file are kept." Implements ITool.Description
        Public ReadOnly Property IsReadOnly As Boolean = False Implements ITool.IsReadOnly

        Public Function Parameters() As JsonObject Implements ITool.Parameters
            Return ToolKit.Schema({"file_path", "content"}, ("file_path", "string", "The file to write"), ("content", "string", "The full content of the file"))
        End Function

        Public Function Title(input As JsonElement, state As AgentState) As String Implements ITool.Title
            Dim full = state.Resolve(ToolKit.Arg(input, "file_path", ""))
            Return If(File.Exists(full), "Write(", "Create(") & state.Display(full) & ")"
        End Function

        Public Function Preview(input As JsonElement, state As AgentState) As String Implements ITool.Preview
            Dim full = state.Resolve(ToolKit.Arg(input, "file_path", ""))
            Dim content = If(ToolKit.Arg(input, "content", ""), "").Replace(ChrW(13) & ChrW(10), ChrW(10))
            If File.Exists(full) Then Return EditLogic.DiffDetail(TextFile.Load(full).Text, content, full, 30).Detail
            Return NewFilePreview(content, full, 20)
        End Function

        Friend Shared Function NewFilePreview(content As String, file As String, maxLines As Integer) As String
            Dim lang = Highlighter.LangOf(file)
            Dim lines = Diff.SplitLines(content)
            Dim sb As New StringBuilder()
            Dim w = Ansi.TermWidth() - 12
            For i = 0 To Math.Min(lines.Length, maxLines) - 1
                sb.Append("      ").Append(Ansi.Dim2((i + 1).ToString().PadLeft(4))).Append("  ").Append(Highlighter.Line(Ansi.Truncate(lines(i).Replace(vbTab, "    "), w), lang)).Append(Environment.NewLine)
            Next
            If lines.Length > maxLines Then sb.Append("      ").Append(Ansi.Muted($"… +{lines.Length - maxLines} lines")).Append(Environment.NewLine)
            Return sb.ToString()
        End Function

        Public Function RunAsync(input As JsonElement, ctx As ToolContext) As Task(Of ToolResult) Implements ITool.RunAsync
            Dim state = ctx.State
            Dim p = ToolKit.Arg(input, "file_path")
            If String.IsNullOrWhiteSpace(p) Then Return Task.FromResult(ToolResult.Fail("file_path is required."))
            If Not Jx.Has(input, "content") Then Return Task.FromResult(ToolResult.Fail("content is required."))
            Dim full = state.Resolve(p)
            If Directory.Exists(full) Then Return Task.FromResult(ToolResult.Fail($"{full} is a directory."))
            Dim stale = EditLogic.CheckFresh(state, full)
            If stale IsNot Nothing Then Return Task.FromResult(ToolResult.Fail(stale))
            Dim content = If(Jx.StrOf(input, "content", ""), "").Replace(ChrW(13) & ChrW(10), ChrW(10))
            Try
                Dim tf = TextFile.Load(full)
                state.Checkpoints?.Snapshot(full)
                Dim existed = tf.Exists
                Dim before = tf.Text
                tf.Save(full, content)
                state.MarkRead(full)
                Dim lineCount = Diff.SplitLines(content).Length
                If existed Then
                    Dim d = EditLogic.DiffDetail(before, content, full)
                    Return Task.FromResult(ToolResult.Done($"The file {full} has been updated.", $"Updated {Ansi.Bold(state.Display(full))} {d.Summary}", d.Detail))
                End If
                Return Task.FromResult(ToolResult.Done($"File created successfully at: {full}", $"Wrote {Ansi.Bold(lineCount.ToString())} lines to {Ansi.Bold(state.Display(full))}", NewFilePreview(content, full, 8)))
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
                Return Task.FromResult(ToolResult.Fail("Write failed: " & ex.Message))
            End Try
        End Function

    End Class

    Friend NotInheritable Class EditTool
        Implements ITool

        Public ReadOnly Property Name As String = "Edit" Implements ITool.Name
        Public ReadOnly Property Description As String = "Replaces exact text in a file. You must Read the file first. old_string must match the file exactly " &
            "(indentation included; do not include the line-number prefix from Read) and be unique in the file unless replace_all is true; " &
            "include enough surrounding lines to make it unique. Use replace_all to rename something throughout the file. Keep the file's style." Implements ITool.Description
        Public ReadOnly Property IsReadOnly As Boolean = False Implements ITool.IsReadOnly

        Public Function Parameters() As JsonObject Implements ITool.Parameters
            Dim s = ToolKit.Schema({"file_path", "old_string", "new_string"}, ("file_path", "string", "The file to change"),
                                   ("old_string", "string", "The exact text to replace"), ("new_string", "string", "The text to put in its place (different from old_string)"),
                                   ("replace_all", "boolean", "Replace every occurrence (default false)"))
            Return s
        End Function

        Public Function Title(input As JsonElement, state As AgentState) As String Implements ITool.Title
            Return "Update(" & state.Display(state.Resolve(ToolKit.Arg(input, "file_path", ""))) & ")"
        End Function

        Public Function Preview(input As JsonElement, state As AgentState) As String Implements ITool.Preview
            Dim full = state.Resolve(ToolKit.Arg(input, "file_path", ""))
            Dim tf = TextFile.Load(full)
            Dim r = EditLogic.Apply(tf.Text, Jx.StrOf(input, "old_string", ""), Jx.StrOf(input, "new_string", ""), ToolKit.ArgBool(input, "replace_all", False))
            If r.Error IsNot Nothing Then Return "      " & Ansi.Warn(r.Error.Split(ChrW(10))(0)) & Environment.NewLine
            Return EditLogic.DiffDetail(tf.Text, r.NewText, full, 30).Detail
        End Function

        Public Function RunAsync(input As JsonElement, ctx As ToolContext) As Task(Of ToolResult) Implements ITool.RunAsync
            Dim state = ctx.State
            Dim p = ToolKit.Arg(input, "file_path")
            If String.IsNullOrWhiteSpace(p) Then Return Task.FromResult(ToolResult.Fail("file_path is required."))
            Dim full = state.Resolve(p)
            Dim oldS = Jx.StrOf(input, "old_string", "")
            If Not File.Exists(full) Then
                If oldS.Length = 0 Then Return New WriteTool().RunAsync(Jx.Parse(Jx.Stringify(New With {.file_path = full, .content = Jx.StrOf(input, "new_string", "")})), ctx)
                Return Task.FromResult(ToolResult.Fail($"File does not exist: {full}"))
            End If
            Dim stale = EditLogic.CheckFresh(state, full)
            If stale IsNot Nothing Then Return Task.FromResult(ToolResult.Fail(stale))
            Try
                Dim tf = TextFile.Load(full)
                Dim newS = Jx.StrOf(input, "new_string", "")
                Dim r = EditLogic.Apply(tf.Text, oldS, newS, ToolKit.ArgBool(input, "replace_all", False))
                If r.Error IsNot Nothing Then Return Task.FromResult(ToolResult.Fail(r.Error, r.Error.Split(ChrW(10))(0)))
                state.Checkpoints?.Snapshot(full)
                tf.Save(full, r.NewText)
                state.MarkRead(full)
                Dim d = EditLogic.DiffDetail(tf.Text, r.NewText, full)
                Dim content = $"The file {full} has been updated. Here's the result of running `cat -n` on a snippet of the edited file:" & vbLf & EditLogic.Snippet(r.NewText, newS.Replace(ChrW(13) & ChrW(10), ChrW(10)))
                Return Task.FromResult(ToolResult.Done(content, $"Updated {Ansi.Bold(state.Display(full))} {d.Summary}", d.Detail))
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
                Return Task.FromResult(ToolResult.Fail("Edit failed: " & ex.Message))
            End Try
        End Function

    End Class

    Friend NotInheritable Class MultiEditTool
        Implements ITool

        Public ReadOnly Property Name As String = "MultiEdit" Implements ITool.Name
        Public ReadOnly Property Description As String = "Makes several edits to one file in one step; each edit works like Edit and applies to the result of the previous one. " &
            "All edits succeed or none is made. Read the file first. Prefer this over several Edit calls on the same file." Implements ITool.Description
        Public ReadOnly Property IsReadOnly As Boolean = False Implements ITool.IsReadOnly

        Public Function Parameters() As JsonObject Implements ITool.Parameters
            Dim item As New JsonObject From {{"type", "object"}, {"properties", New JsonObject From {
                {"old_string", New JsonObject From {{"type", "string"}}}, {"new_string", New JsonObject From {{"type", "string"}}},
                {"replace_all", New JsonObject From {{"type", "boolean"}}}}}, {"required", New JsonArray("old_string", "new_string")}}
            Return New JsonObject From {{"type", "object"}, {"properties", New JsonObject From {
                {"file_path", New JsonObject From {{"type", "string"}, {"description", "The file to change"}}},
                {"edits", New JsonObject From {{"type", "array"}, {"items", item}, {"description", "Edits to apply in order"}}}}},
                {"required", New JsonArray("file_path", "edits")}}
        End Function

        Public Function Title(input As JsonElement, state As AgentState) As String Implements ITool.Title
            Return "Update(" & state.Display(state.Resolve(ToolKit.Arg(input, "file_path", ""))) & ")"
        End Function

        Private Shared Function Compute(input As JsonElement, text As String) As (NewText As String, [Error] As String)
            Dim current = text
            Dim n = 0
            For Each e In Jx.ArrOf(input, "edits")
                n += 1
                Dim r = EditLogic.Apply(current, Jx.StrOf(e, "old_string", ""), Jx.StrOf(e, "new_string", ""), Jx.BoolOf(e, "replace_all"))
                If r.Error IsNot Nothing Then Return (Nothing, $"Edit {n}: {r.Error}")
                current = r.NewText
            Next
            If n = 0 Then Return (Nothing, "edits is empty.")
            Return (current, Nothing)
        End Function

        Public Function Preview(input As JsonElement, state As AgentState) As String Implements ITool.Preview
            Dim full = state.Resolve(ToolKit.Arg(input, "file_path", ""))
            Dim tf = TextFile.Load(full)
            Dim r = Compute(input, tf.Text)
            If r.Error IsNot Nothing Then Return "      " & Ansi.Warn(r.Error.Split(ChrW(10))(0)) & Environment.NewLine
            Return EditLogic.DiffDetail(tf.Text, r.NewText, full, 30).Detail
        End Function

        Public Function RunAsync(input As JsonElement, ctx As ToolContext) As Task(Of ToolResult) Implements ITool.RunAsync
            Dim state = ctx.State
            Dim full = state.Resolve(ToolKit.Arg(input, "file_path", ""))
            Dim stale = EditLogic.CheckFresh(state, full)
            If stale IsNot Nothing Then Return Task.FromResult(ToolResult.Fail(stale))
            Try
                Dim tf = TextFile.Load(full)
                Dim r = Compute(input, tf.Text)
                If r.Error IsNot Nothing Then Return Task.FromResult(ToolResult.Fail(r.Error, r.Error.Split(ChrW(10))(0)))
                state.Checkpoints?.Snapshot(full)
                tf.Save(full, r.NewText)
                state.MarkRead(full)
                Dim d = EditLogic.DiffDetail(tf.Text, r.NewText, full)
                Dim count = Jx.ArrOf(input, "edits").Count
                Return Task.FromResult(ToolResult.Done($"Applied {count} edits to {full}.", $"Updated {Ansi.Bold(state.Display(full))} {d.Summary}", d.Detail))
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
                Return Task.FromResult(ToolResult.Fail("Edit failed: " & ex.Message))
            End Try
        End Function

    End Class

    Friend NotInheritable Class NotebookEditTool
        Implements ITool

        Public ReadOnly Property Name As String = "NotebookEdit" Implements ITool.Name
        Public ReadOnly Property Description As String = "Replaces, inserts or deletes a cell in a Jupyter notebook (.ipynb). Identify the cell by cell_id (from Read) or cell_number (0-based). " &
            "edit_mode: replace (default), insert (a new cell after the given one, or at the start when none is given) or delete." Implements ITool.Description
        Public ReadOnly Property IsReadOnly As Boolean = False Implements ITool.IsReadOnly

        Public Function Parameters() As JsonObject Implements ITool.Parameters
            Return ToolKit.Schema({"notebook_path", "new_source"}, ("notebook_path", "string", "The notebook"), ("cell_id", "string", "The cell's id"),
                                  ("cell_number", "integer", "The cell's index (0-based)"), ("new_source", "string", "The new source of the cell"),
                                  ("cell_type", "string", "code or markdown (needed for insert)"), ("edit_mode", "string", "replace | insert | delete"))
        End Function

        Public Function Title(input As JsonElement, state As AgentState) As String Implements ITool.Title
            Return "NotebookEdit(" & state.Display(state.Resolve(ToolKit.Arg(input, "notebook_path", ""))) & ")"
        End Function

        Public Function Preview(input As JsonElement, state As AgentState) As String Implements ITool.Preview
            Return "      " & Ansi.Muted(ToolKit.Arg(input, "edit_mode", "replace") & " cell " & ToolKit.Arg(input, "cell_id", ToolKit.Arg(input, "cell_number", "?"))) & Environment.NewLine &
                   WriteTool.NewFilePreview(ToolKit.Arg(input, "new_source", ""), "x.py", 12)
        End Function

        Public Function RunAsync(input As JsonElement, ctx As ToolContext) As Task(Of ToolResult) Implements ITool.RunAsync
            Dim state = ctx.State
            Dim full = state.Resolve(ToolKit.Arg(input, "notebook_path", ""))
            If Not File.Exists(full) Then Return Task.FromResult(ToolResult.Fail("Notebook does not exist: " & full))
            Dim stale = EditLogic.CheckFresh(state, full)
            If stale IsNot Nothing Then Return Task.FromResult(ToolResult.Fail(stale))
            Try
                Dim nb = Jx.ParseObject(File.ReadAllText(full))
                Dim cells = TryCast(nb("cells"), JsonArray)
                If cells Is Nothing Then Return Task.FromResult(ToolResult.Fail("Not a Jupyter notebook."))
                Dim mode = ToolKit.Arg(input, "edit_mode", "replace").ToLowerInvariant()
                Dim id = ToolKit.Arg(input, "cell_id")
                Dim index = -1
                If id IsNot Nothing Then
                    For i = 0 To cells.Count - 1
                        If Jx.NodeStr(cells(i)?("id"), "") = id Then index = i : Exit For
                    Next
                    If index < 0 AndAlso id.StartsWith("cell-") Then Integer.TryParse(id.Substring(5), index)
                    If index < 0 Then Return Task.FromResult(ToolResult.Fail("No cell with id " & id))
                ElseIf Jx.Has(input, "cell_number") Then
                    index = ToolKit.ArgInt(input, "cell_number", -1)
                End If
                Dim source = ToolKit.Arg(input, "new_source", "").Replace(ChrW(13) & ChrW(10), ChrW(10))
                Dim srcLines = source.Split(ChrW(10))
                Dim srcArray As New JsonArray(srcLines.Select(Function(l, i) CType(JsonValue.Create(If(i < srcLines.Length - 1, l & vbLf, l)), JsonNode)).ToArray())
                state.Checkpoints?.Snapshot(full)
                Select Case mode
                    Case "delete"
                        If index < 0 OrElse index >= cells.Count Then Return Task.FromResult(ToolResult.Fail("Cell not found."))
                        cells.RemoveAt(index)
                    Case "insert"
                        Dim type = ToolKit.Arg(input, "cell_type", "code")
                        Dim cell As New JsonObject From {{"cell_type", type}, {"id", Guid.NewGuid().ToString("N").Substring(0, 8)}, {"metadata", New JsonObject()}, {"source", srcArray}}
                        If type = "code" Then
                            cell("outputs") = New JsonArray()
                            cell("execution_count") = Nothing
                        End If
                        cells.Insert(Math.Min(cells.Count, index + 1), cell)
                    Case Else
                        If index < 0 OrElse index >= cells.Count Then Return Task.FromResult(ToolResult.Fail("Cell not found."))
                        Dim cell = DirectCast(cells(index), JsonObject)
                        cell("source") = srcArray
                        If Jx.Has(input, "cell_type") Then cell("cell_type") = ToolKit.Arg(input, "cell_type")
                        If Jx.NodeStr(cell("cell_type"), "code") = "code" Then
                            cell("outputs") = New JsonArray()
                            cell("execution_count") = Nothing
                        End If
                End Select
                File.WriteAllText(full, nb.ToJsonString(New JsonSerializerOptions With {.WriteIndented = True}) & vbLf, New UTF8Encoding(False))
                state.MarkRead(full)
                Return Task.FromResult(ToolResult.Done($"Notebook updated ({mode}).", $"{mode} cell in {Ansi.Bold(state.Display(full))}"))
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is JsonException OrElse TypeOf ex Is InvalidCastException
                Return Task.FromResult(ToolResult.Fail("NotebookEdit failed: " & ex.Message))
            End Try
        End Function

    End Class

    Friend NotInheritable Class LsTool
        Implements ITool

        Public ReadOnly Property Name As String = "LS" Implements ITool.Name
        Public ReadOnly Property Description As String = "Lists files and folders under a directory as a tree (ignored and build folders are skipped). " &
            "path may be absolute or relative; ignore takes glob patterns to leave out. Prefer Glob and Grep when you know what you look for." Implements ITool.Description
        Public ReadOnly Property IsReadOnly As Boolean = True Implements ITool.IsReadOnly

        Public Function Parameters() As JsonObject Implements ITool.Parameters
            Return ToolKit.Schema(Nothing, ("path", "string", "The directory (default: the working directory)"), ("ignore", "array", "Glob patterns to leave out"))
        End Function

        Public Function Title(input As JsonElement, state As AgentState) As String Implements ITool.Title
            Return "List(" & state.Display(state.Resolve(ToolKit.Arg(input, "path", "."))) & ")"
        End Function

        Public Function Preview(input As JsonElement, state As AgentState) As String Implements ITool.Preview
            Return Nothing
        End Function

        Public Function RunAsync(input As JsonElement, ctx As ToolContext) As Task(Of ToolResult) Implements ITool.RunAsync
            Dim root = ctx.State.Resolve(ToolKit.Arg(input, "path", "."))
            If Not Directory.Exists(root) Then Return Task.FromResult(ToolResult.Fail("Directory does not exist: " & root))
            Dim ignores = Jx.StringsOf(input, "ignore").Select(Function(g) FileWalker.GlobToRegex(g)).ToList()
            Dim walker As New FileWalker(root)
            Dim entries As New List(Of String)
            Dim truncated = False
            For Each e In walker.Walk(includeDirs:=True, cancel:=ctx.Cancel)
                Dim rel = Path.GetRelativePath(root, e.FullPath).Replace("\"c, "/"c)
                If ignores.Any(Function(r) r.IsMatch(rel) OrElse r.IsMatch(Path.GetFileName(e.FullPath))) Then Continue For
                entries.Add(rel & If(e.IsDir, "/", ""))
                If entries.Count >= 1000 Then truncated = True : Exit For
            Next
            entries.Sort(StringComparer.OrdinalIgnoreCase)
            Dim sb As New StringBuilder()
            sb.Append("- ").Append(root.TrimEnd("\"c)).Append("\").Append(vbLf)
            For Each rel In entries
                Dim parts = rel.TrimEnd("/"c).Split("/"c)
                sb.Append(New String(" "c, parts.Length * 2)).Append("- ").Append(parts.Last()).Append(If(rel.EndsWith("/"), "/", "")).Append(vbLf)
            Next
            If truncated Then sb.Append(vbLf & "(More than 1000 entries; list a subfolder or use Glob.)")
            Return Task.FromResult(ToolResult.Done(sb.ToString(), $"Listed {Ansi.Bold(entries.Count.ToString())} paths"))
        End Function

    End Class

End Namespace
