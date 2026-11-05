Option Strict On
Option Explicit On
Option Infer On

Imports System.IO
Imports System.Text
Imports System.Text.Json
Imports System.Text.Json.Nodes
Imports System.Text.RegularExpressions
Imports System.Threading
Imports AgenticCli.Agent
Imports AgenticCli.Core
Imports AgenticCli.Ui

Namespace Tools

    Friend Structure WalkEntry
        Public FullPath As String
        Public IsDir As Boolean
    End Structure

    ''' <summary>
    ''' Walks a folder like ripgrep does: .gitignore files are honoured (nested ones too, with ! exceptions), and .git,
    ''' node_modules, bin, obj and other build/tool folders are skipped unless the walk starts inside them.
    ''' </summary>
    Friend NotInheritable Class FileWalker

        Public Shared ReadOnly SkipDirs As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase) From {
            ".git", "node_modules", ".vs", ".idea", "__pycache__", ".venv", "venv", "bin", "obj", ".mypy_cache", ".pytest_cache", ".next", ".nuget", ".gradle", ".tox", ".svn", ".hg"}

        Private NotInheritable Class Rule
            Public Base As String
            Public Pattern As Regex
            Public Negate As Boolean
            Public DirOnly As Boolean
        End Class

        Private ReadOnly _root As String
        Public Property MaxDepth As Integer = Integer.MaxValue
        Public Property RespectIgnore As Boolean = True

        Public Sub New(root As String)
            _root = Path.GetFullPath(root)
        End Sub

        Public Iterator Function Walk(includeDirs As Boolean, cancel As CancellationToken) As IEnumerable(Of WalkEntry)
            Dim rootRules = If(RespectIgnore, ParentRules(_root), New List(Of Rule))
            Dim stack As New Stack(Of (Dir As String, Rules As List(Of Rule), Depth As Integer))
            stack.Push((_root, rootRules, 0))
            While stack.Count > 0
                If cancel.IsCancellationRequested Then Exit Function
                Dim cur = stack.Pop()
                Dim rules = cur.Rules
                If RespectIgnore Then
                    Dim gi = Path.Combine(cur.Dir, ".gitignore")
                    If File.Exists(gi) Then rules = rules.Concat(Parse(Paths.ReadText(gi), cur.Dir)).ToList()
                End If
                Dim entries As IEnumerable(Of FileSystemInfo)
                Try
                    entries = New DirectoryInfo(cur.Dir).EnumerateFileSystemInfos("*", New EnumerationOptions With {.IgnoreInaccessible = True, .AttributesToSkip = FileAttributes.System Or FileAttributes.ReparsePoint}).ToList()
                Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
                    Continue While
                End Try
                Dim subdirs As New List(Of String)
                For Each e In entries
                    Dim isDir = (e.Attributes And FileAttributes.Directory) <> 0
                    If isDir AndAlso SkipDirs.Contains(e.Name) Then Continue For
                    If RespectIgnore AndAlso Ignored(rules, e.FullName, isDir) Then Continue For
                    If isDir Then
                        subdirs.Add(e.FullName)
                        If includeDirs Then Yield New WalkEntry With {.FullPath = e.FullName, .IsDir = True}
                    Else
                        Yield New WalkEntry With {.FullPath = e.FullName, .IsDir = False}
                    End If
                Next
                If cur.Depth + 1 < MaxDepth Then
                    subdirs.Sort(StringComparer.OrdinalIgnoreCase)
                    For i = subdirs.Count - 1 To 0 Step -1
                        stack.Push((subdirs(i), rules, cur.Depth + 1))
                    Next
                End If
            End While
        End Function

        ''' <summary>.gitignore files between the repository root and the start folder also apply.</summary>
        Private Shared Function ParentRules(start As String) As List(Of Rule)
            Dim chain As New List(Of String)
            Dim d = Directory.GetParent(start)
            While d IsNot Nothing
                chain.Add(d.FullName)
                If Directory.Exists(Path.Combine(d.FullName, ".git")) Then Exit While
                d = d.Parent
                If chain.Count > 12 Then Return New List(Of Rule)
            End While
            If chain.Count = 0 OrElse Not Directory.Exists(Path.Combine(chain.Last(), ".git")) Then Return New List(Of Rule)
            Dim rules As New List(Of Rule)
            For i = chain.Count - 1 To 0 Step -1
                Dim gi = Path.Combine(chain(i), ".gitignore")
                If File.Exists(gi) Then rules.AddRange(Parse(Paths.ReadText(gi), chain(i)))
            Next
            Return rules
        End Function

        Private Shared Function Parse(text As String, baseDir As String) As List(Of Rule)
            Dim list As New List(Of Rule)
            For Each raw In If(text, "").Replace(ChrW(13), "").Split(ChrW(10))
                Dim p = raw.TrimEnd()
                If p.Length = 0 OrElse p.StartsWith("#") Then Continue For
                Dim r As New Rule With {.Base = baseDir}
                If p.StartsWith("!") Then r.Negate = True : p = p.Substring(1)
                If p.StartsWith("\") Then p = p.Substring(1)
                If p.EndsWith("/") Then r.DirOnly = True : p = p.TrimEnd("/"c)
                If p.Length = 0 Then Continue For
                Dim anchored = p.Contains("/"c)
                p = p.TrimStart("/"c)
                Dim body = Convert(p)
                Try
                    r.Pattern = New Regex(If(anchored, "^", "(?:^|/)") & body & "(?:/.*)?$", RegexOptions.IgnoreCase Or RegexOptions.CultureInvariant)
                    list.Add(r)
                Catch ex As ArgumentException
                End Try
            Next
            Return list
        End Function

        Private Shared Function Ignored(rules As List(Of Rule), full As String, isDir As Boolean) As Boolean
            Dim result = False
            For Each r In rules
                If r.DirOnly AndAlso Not isDir Then Continue For
                Dim rel = Path.GetRelativePath(r.Base, full).Replace("\"c, "/"c)
                If rel.StartsWith("..") Then Continue For
                If r.Pattern.IsMatch(rel) Then result = Not r.Negate
            Next
            Return result
        End Function

        ''' <summary>Glob syntax (**, *, ?, [abc], {a,b}) to a regex body over '/'-separated paths.</summary>
        Friend Shared Function Convert(glob As String) As String
            Dim sb As New StringBuilder()
            Dim i = 0
            While i < glob.Length
                Dim c = glob(i)
                If c = "*"c Then
                    If i + 1 < glob.Length AndAlso glob(i + 1) = "*"c Then
                        If i + 2 < glob.Length AndAlso glob(i + 2) = "/"c Then
                            sb.Append("(?:.*/)?")
                            i += 3
                        Else
                            sb.Append(".*")
                            i += 2
                        End If
                        Continue While
                    End If
                    sb.Append("[^/]*")
                ElseIf c = "?"c Then
                    sb.Append("[^/]")
                ElseIf c = "["c Then
                    Dim close = glob.IndexOf("]"c, i + 1)
                    If close > i Then
                        sb.Append("[").Append(glob.Substring(i + 1, close - i - 1).Replace("!", "^").Replace("\", "\\")).Append("]")
                        i = close + 1
                        Continue While
                    End If
                    sb.Append("\[")
                ElseIf c = "{"c Then
                    Dim close = glob.IndexOf("}"c, i + 1)
                    If close > i Then
                        sb.Append("(?:").Append(String.Join("|", glob.Substring(i + 1, close - i - 1).Split(","c).Select(Function(x) Convert(x)))).Append(")")
                        i = close + 1
                        Continue While
                    End If
                    sb.Append("\{")
                ElseIf c = "\"c OrElse c = "/"c Then
                    sb.Append("/")
                Else
                    sb.Append(Regex.Escape(c.ToString()))
                End If
                i += 1
            End While
            Return sb.ToString()
        End Function

        ''' <summary>A glob over relative paths; a pattern without '/' matches file names at the top level only, like a shell.</summary>
        Public Shared Function GlobToRegex(glob As String) As Regex
            Return New Regex("^" & Convert(glob.Replace("\"c, "/"c).TrimStart("."c, "/"c)) & "$", RegexOptions.IgnoreCase Or RegexOptions.CultureInvariant)
        End Function

        ''' <summary>How deep a glob can reach (no ** = the number of '/' plus one).</summary>
        Public Shared Function DepthOf(glob As String) As Integer
            If glob.Contains("**") Then Return Integer.MaxValue
            Return glob.Replace("\"c, "/"c).TrimStart("."c, "/"c).Count(Function(c) c = "/"c) + 1
        End Function

    End Class

    Friend NotInheritable Class GlobTool
        Implements ITool

        Public ReadOnly Property Name As String = "Glob" Implements ITool.Name
        Public ReadOnly Property Description As String = "Finds files by name pattern, fast, in any size of codebase: ""**/*.vb"", ""src/**/*.{ts,tsx}"", ""*.json"" (top level). " &
            "Returns matching paths, most recently modified first (at most 100). .gitignore'd and build folders are skipped. " &
            "Use it to find files by name; use Grep to search their contents; for open-ended searches across many rounds use the Task tool." Implements ITool.Description
        Public ReadOnly Property IsReadOnly As Boolean = True Implements ITool.IsReadOnly

        Public Function Parameters() As JsonObject Implements ITool.Parameters
            Return ToolKit.Schema({"pattern"}, ("pattern", "string", "The glob pattern"), ("path", "string", "The folder to search (default: the working directory)"))
        End Function

        Public Function Title(input As JsonElement, state As AgentState) As String Implements ITool.Title
            Dim p = ToolKit.Arg(input, "path")
            Return $"Search(pattern: ""{ToolKit.Arg(input, "pattern", "")}""" & If(p Is Nothing, "", $", path: ""{state.Display(state.Resolve(p))}""") & ")"
        End Function

        Public Function Preview(input As JsonElement, state As AgentState) As String Implements ITool.Preview
            Return Nothing
        End Function

        Public Function RunAsync(input As JsonElement, ctx As ToolContext) As Task(Of ToolResult) Implements ITool.RunAsync
            Return Task.Run(Function() Run(input, ctx), ctx.Cancel)
        End Function

        Private Function Run(input As JsonElement, ctx As ToolContext) As ToolResult
            Dim pattern = ToolKit.Arg(input, "pattern")
            If String.IsNullOrWhiteSpace(pattern) Then Return ToolResult.Fail("pattern is required.")
            Dim root = ctx.State.Resolve(ToolKit.Arg(input, "path", "."))
            ' An absolute pattern: split it into folder and pattern.
            If Path.IsPathRooted(pattern) Then
                Dim firstWild = pattern.IndexOfAny({"*"c, "?"c, "["c, "{"c})
                Dim cut = If(firstWild < 0, pattern.Length, pattern.LastIndexOfAny({"\"c, "/"c}, firstWild))
                root = pattern.Substring(0, Math.Max(0, cut))
                pattern = pattern.Substring(Math.Min(pattern.Length, cut + 1))
            End If
            If Not Directory.Exists(root) Then Return ToolResult.Fail("Directory does not exist: " & root)
            Dim rx = FileWalker.GlobToRegex(pattern)
            Dim walker As New FileWalker(root) With {.MaxDepth = FileWalker.DepthOf(pattern)}
            Dim matches As New List(Of (Path As String, Time As DateTime))
            For Each e In walker.Walk(False, ctx.Cancel)
                Dim rel = Path.GetRelativePath(root, e.FullPath).Replace("\"c, "/"c)
                If rx.IsMatch(rel) Then
                    matches.Add((e.FullPath, File.GetLastWriteTimeUtc(e.FullPath)))
                    If matches.Count >= 20000 Then Exit For
                End If
            Next
            If matches.Count = 0 Then Return ToolResult.Done("No files found", "Found 0 files")
            Dim sorted = matches.OrderByDescending(Function(m) m.Time).Select(Function(m) m.Path).ToList()
            Dim shown = sorted.Take(100).ToList()
            Dim text = String.Join(vbLf, shown) & If(sorted.Count > 100, $"{vbLf}(Results are truncated: {sorted.Count} files matched. Use a more specific path or pattern.)", "")
            Return ToolResult.Done(text, $"Found {Ansi.Bold(sorted.Count.ToString())} file{If(sorted.Count = 1, "", "s")}")
        End Function

    End Class

    Friend NotInheritable Class GrepTool
        Implements ITool

        Private Shared ReadOnly Types As New Dictionary(Of String, String())(StringComparer.OrdinalIgnoreCase) From {
            {"js", {"*.js", "*.jsx", "*.mjs", "*.cjs"}}, {"ts", {"*.ts", "*.tsx", "*.mts", "*.cts"}}, {"py", {"*.py", "*.pyi"}}, {"cs", {"*.cs", "*.csx"}},
            {"vb", {"*.vb"}}, {"java", {"*.java"}}, {"go", {"*.go"}}, {"rust", {"*.rs"}}, {"rs", {"*.rs"}}, {"c", {"*.c", "*.h"}},
            {"cpp", {"*.cpp", "*.cc", "*.cxx", "*.hpp", "*.hh", "*.h"}}, {"json", {"*.json"}}, {"md", {"*.md", "*.markdown"}}, {"html", {"*.html", "*.htm"}},
            {"css", {"*.css", "*.scss", "*.less"}}, {"xml", {"*.xml", "*.xaml", "*.csproj", "*.vbproj", "*.props", "*.targets"}}, {"yaml", {"*.yml", "*.yaml"}},
            {"sh", {"*.sh", "*.bash"}}, {"ps1", {"*.ps1", "*.psm1", "*.psd1"}}, {"sql", {"*.sql"}}, {"rb", {"*.rb"}}, {"php", {"*.php"}},
            {"kotlin", {"*.kt", "*.kts"}}, {"swift", {"*.swift"}}, {"toml", {"*.toml"}}}

        Public ReadOnly Property Name As String = "Grep" Implements ITool.Name
        Public ReadOnly Property Description As String = "Searches file contents with a regular expression (.NET syntax, like ripgrep). " &
            "output_mode: files_with_matches (default, paths only), content (matching lines; -n for line numbers, -A/-B/-C for context), count. " &
            "Filter files with glob (""*.vb"", ""**/*.{ts,tsx}"") or type (js, ts, py, cs, vb, java, go, rust, ...). -i ignores case; multiline lets . match newlines and patterns span lines. " &
            "head_limit caps the results. Escape regex characters to search literally (e.g. interface\{\})." Implements ITool.Description
        Public ReadOnly Property IsReadOnly As Boolean = True Implements ITool.IsReadOnly

        Public Function Parameters() As JsonObject Implements ITool.Parameters
            Return ToolKit.Schema({"pattern"}, ("pattern", "string", "The regular expression"), ("path", "string", "File or folder to search (default: the working directory)"),
                                  ("glob", "string", "Only files matching this glob"), ("type", "string", "Only files of this type (js, py, cs, vb, ...)"),
                                  ("output_mode", "string", "files_with_matches | content | count"), ("-i", "boolean", "Ignore case"),
                                  ("-n", "boolean", "Line numbers (content mode; default true)"), ("-A", "integer", "Lines after each match"),
                                  ("-B", "integer", "Lines before each match"), ("-C", "integer", "Lines before and after"),
                                  ("head_limit", "integer", "At most this many results"), ("multiline", "boolean", "Patterns may span lines"))
        End Function

        Public Function Title(input As JsonElement, state As AgentState) As String Implements ITool.Title
            Dim p = ToolKit.Arg(input, "path")
            Return $"Search(pattern: ""{ToolKit.OneLine(ToolKit.Arg(input, "pattern", ""), 60)}""" & If(p Is Nothing, "", $", path: ""{state.Display(state.Resolve(p))}""") &
                   If(ToolKit.Arg(input, "glob") Is Nothing, "", $", glob: ""{ToolKit.Arg(input, "glob")}""") & ")"
        End Function

        Public Function Preview(input As JsonElement, state As AgentState) As String Implements ITool.Preview
            Return Nothing
        End Function

        Public Function RunAsync(input As JsonElement, ctx As ToolContext) As Task(Of ToolResult) Implements ITool.RunAsync
            Return Task.Run(Function() Run(input, ctx), ctx.Cancel)
        End Function

        Private Function Run(input As JsonElement, ctx As ToolContext) As ToolResult
            Dim pattern = ToolKit.Arg(input, "pattern")
            If String.IsNullOrEmpty(pattern) Then Return ToolResult.Fail("pattern is required.")
            Dim opts = RegexOptions.CultureInvariant
            If ToolKit.ArgBool(input, "-i", False) Then opts = opts Or RegexOptions.IgnoreCase
            Dim multiline = ToolKit.ArgBool(input, "multiline", False)
            If multiline Then opts = opts Or RegexOptions.Singleline Or RegexOptions.Multiline
            Dim rx As Regex
            Try
                rx = New Regex(pattern, opts, TimeSpan.FromSeconds(2))
            Catch ex As ArgumentException
                Return ToolResult.Fail("Invalid regular expression: " & ex.Message)
            End Try
            Dim target = ctx.State.Resolve(ToolKit.Arg(input, "path", "."))
            Dim mode = ToolKit.Arg(input, "output_mode", "files_with_matches").ToLowerInvariant()
            Dim showNumbers = ToolKit.ArgBool(input, "-n", True)
            Dim ctxC = ToolKit.ArgInt(input, "-C", 0)
            Dim after = Math.Max(ToolKit.ArgInt(input, "-A", 0), ctxC)
            Dim before = Math.Max(ToolKit.ArgInt(input, "-B", 0), ctxC)
            Dim limit = ToolKit.ArgInt(input, "head_limit", 0)
            Dim globRx As Regex = Nothing
            Dim globOnName = False
            Dim g = ToolKit.Arg(input, "glob")
            If g IsNot Nothing Then
                globOnName = Not g.Contains("/"c) AndAlso Not g.Contains("\"c)
                globRx = If(globOnName, New Regex("^" & FileWalker.Convert(g) & "$", RegexOptions.IgnoreCase), FileWalker.GlobToRegex(g))
            End If
            Dim typePatterns As Regex = Nothing
            Dim t = ToolKit.Arg(input, "type")
            If t IsNot Nothing Then
                Dim globs As String() = Nothing
                If Not Types.TryGetValue(t, globs) Then globs = {"*." & t}
                typePatterns = New Regex("^(?:" & String.Join("|", globs.Select(Function(x) FileWalker.Convert(x))) & ")$", RegexOptions.IgnoreCase)
            End If

            Dim files As New List(Of String)
            If File.Exists(target) Then
                files.Add(target)
            ElseIf Directory.Exists(target) Then
                For Each e In New FileWalker(target).Walk(False, ctx.Cancel)
                    Dim nm = Path.GetFileName(e.FullPath)
                    If globRx IsNot Nothing AndAlso Not globRx.IsMatch(If(globOnName, nm, Path.GetRelativePath(target, e.FullPath).Replace("\"c, "/"c))) Then Continue For
                    If typePatterns IsNot Nothing AndAlso Not typePatterns.IsMatch(nm) Then Continue For
                    files.Add(e.FullPath)
                Next
            Else
                Return ToolResult.Fail("Path does not exist: " & target)
            End If

            Dim out As New List(Of String)
            Dim fileHits As New List(Of (Path As String, Count As Integer, Time As DateTime))
            Dim totalMatches = 0
            For Each f In files
                If ctx.Cancel.IsCancellationRequested Then Exit For
                Dim info As FileInfo
                Try
                    info = New FileInfo(f)
                    If info.Length > 10L * 1024 * 1024 OrElse TextFile.IsBinary(f) Then Continue For
                Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
                    Continue For
                End Try
                Dim text As String
                Try
                    text = TextFile.Load(f).Text
                Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
                    Continue For
                End Try
                Dim count = 0
                Try
                    If multiline Then
                        Dim ms = rx.Matches(text)
                        count = ms.Count
                        If count > 0 AndAlso mode = "content" Then
                            For Each m As Match In ms
                                Dim lineNo = text.Substring(0, m.Index).Count(Function(c) c = ChrW(10)) + 1
                                out.Add(f & If(showNumbers, ":" & lineNo, "") & ":" & m.Value.Replace(vbLf, "\n"))
                            Next
                        End If
                    Else
                        Dim lines = text.Split(ChrW(10))
                        Dim lastPrinted = -1
                        For i = 0 To lines.Length - 1
                            If Not rx.IsMatch(lines(i)) Then Continue For
                            count += 1
                            If mode <> "content" Then Continue For
                            Dim a = Math.Max(0, i - before), b = Math.Min(lines.Length - 1, i + after)
                            If lastPrinted >= 0 AndAlso a > lastPrinted + 1 AndAlso (before > 0 OrElse after > 0) Then out.Add("--")
                            For j = Math.Max(a, lastPrinted + 1) To b
                                Dim sep = If(j = i OrElse rx.IsMatch(lines(j)), ":", "-")
                                Dim l = lines(j)
                                If l.Length > 500 Then l = l.Substring(0, 500) & "…"
                                out.Add(f & If(showNumbers, sep & (j + 1), "") & sep & l)
                            Next
                            lastPrinted = b
                        Next
                    End If
                Catch ex As RegexMatchTimeoutException
                    Continue For
                End Try
                If count > 0 Then
                    fileHits.Add((f, count, info.LastWriteTimeUtc))
                    totalMatches += count
                End If
                If mode = "content" AndAlso limit > 0 AndAlso out.Count >= limit Then Exit For
            Next

            Select Case mode
                Case "content"
                    If out.Count = 0 Then Return ToolResult.Done("No matches found", "Found 0 matches")
                    Dim lines = If(limit > 0, out.Take(limit).ToList(), out)
                    Dim body = ToolKit.Clip(String.Join(vbLf, lines), 40000)
                    Return ToolResult.Done(body, $"Found {Ansi.Bold(totalMatches.ToString())} match{If(totalMatches = 1, "", "es")} in {fileHits.Count} file{If(fileHits.Count = 1, "", "s")}")
                Case "count"
                    If fileHits.Count = 0 Then Return ToolResult.Done("No matches found", "Found 0 matches")
                    Dim list = fileHits.Select(Function(x) x.Path & ":" & x.Count)
                    If limit > 0 Then list = list.Take(limit)
                    Return ToolResult.Done(String.Join(vbLf, list) & vbLf & $"{vbLf}Found {totalMatches} total occurrences across {fileHits.Count} files.", $"Found {Ansi.Bold(totalMatches.ToString())} matches in {fileHits.Count} files")
                Case Else
                    If fileHits.Count = 0 Then Return ToolResult.Done("No files found", "Found 0 files")
                    Dim paths = fileHits.OrderByDescending(Function(x) x.Time).Select(Function(x) x.Path).ToList()
                    Dim shown = If(limit > 0, paths.Take(limit).ToList(), paths.Take(200).ToList())
                    Return ToolResult.Done($"Found {paths.Count} file{If(paths.Count = 1, "", "s")}" & vbLf & String.Join(vbLf, shown) & If(shown.Count < paths.Count, $"{vbLf}(… {paths.Count - shown.Count} more)", ""),
                                           $"Found {Ansi.Bold(paths.Count.ToString())} file{If(paths.Count = 1, "", "s")}")
            End Select
        End Function

    End Class

End Namespace
