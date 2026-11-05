Option Strict On
Option Explicit On
Option Infer On

Imports System.IO
Imports System.IO.Compression
Imports System.Net
Imports System.Net.Sockets
Imports System.Security.Cryptography
Imports System.Text
Imports System.Text.Json
Imports System.Text.Json.Nodes
Imports System.Text.RegularExpressions
Imports System.Threading

Namespace Tools

    ''' <summary>A way to run a project: a dev server, the app, its tests, or the built-in server for a static site.</summary>
    Friend NotInheritable Class RunConfig
        Public Property Id As String
        Public Property Name As String
        ''' <summary>The command line (in the session's shell); Nothing for the built-in static server.</summary>
        Public Property Command As String
        Public Property Cwd As String
        ''' <summary>web (serves a page) | static (the built-in server) | app | test</summary>
        Public Property Kind As String
        Public Property Port As Integer
        Public Property Url As String
        ''' <summary>.agentic/launch.json, .claude/launch.json or "detected".</summary>
        Public Property Source As String
        Public Property Project As String
        Public Property ProjectName As String
    End Class

    ''' <summary>A project (the session's folder or one added with /add-dir) and what Share uploads ("." = the folder; Nothing = nothing yet).</summary>
    Friend NotInheritable Class RunProject
        Public Property Id As String
        Public Property Name As String
        Public Property Folder As String
        Public Property Share As String
        Public Property Key As String
    End Class

    ''' <summary>A started configuration: its shell (or the built-in server), its address and how it ended.</summary>
    Friend NotInheritable Class ProjectRun
        Public Property Config As RunConfig
        Public Property Shell As BackgroundShell
        Public Property Site As StaticSite
        Public Property Url As String
        Public Property Started As DateTime
        Public Property Stopped As Boolean
        ''' <summary>How far the output was searched for the address.</summary>
        Public Property Scanned As Integer

        Public ReadOnly Property Running As Boolean
            Get
                Return Not Stopped AndAlso (Site IsNot Nothing OrElse (Shell IsNot Nothing AndAlso Shell.Running))
            End Get
        End Property

        Public ReadOnly Property ExitCode As Integer?
            Get
                If Running Then Return Nothing
                If Shell IsNot Nothing AndAlso Shell.ExitCode.HasValue Then Return Shell.ExitCode
                Return If(Site IsNot Nothing, 0, -1)
            End Get
        End Property
    End Class

    ''' <summary>
    ''' /run: runs the session's projects like the desktop app's Run button and Claude Desktop's preview. How to run them comes from
    ''' .agentic/launch.json or Claude Code's .claude/launch.json, else from their files (package.json scripts, a static site served
    ''' by a small built-in server, Python, .NET, Go, Rust, a Makefile). Commands run as background shells (see /bashes); a web app's
    ''' address comes from its output, or from its port once it answers.
    ''' </summary>
    Friend NotInheritable Class ProjectRuns

        Private ReadOnly _shells As ShellManager
        Private ReadOnly _runs As New Dictionary(Of String, ProjectRun)(StringComparer.OrdinalIgnoreCase)
        Private Shared ReadOnly UrlPattern As New Regex("https?://(?:localhost|127\.0\.0\.1|0\.0\.0\.0|\[::1?\]|[a-z0-9-]+\.local)(?::\d{2,5})?(?:/[^\s""'<>)\]]*)?", RegexOptions.IgnoreCase)
        Private Shared ReadOnly AnsiCodes As New Regex("\x1B\[[0-9;?]*[ -/]*[@-~]")

        Public Sub New(shells As ShellManager)
            _shells = shells
        End Sub

        ' ------------------------------------------------------------------ configurations

        Public Shared Function Projects(roots As IEnumerable(Of String)) As List(Of RunProject)
            Return roots.Where(Function(r) Not String.IsNullOrWhiteSpace(r) AndAlso Directory.Exists(r)).Distinct(StringComparer.OrdinalIgnoreCase).Take(6).
                Select(Function(r) New RunProject With {.Id = ProjectId(r), .Name = Path.GetFileName(r.TrimEnd(Path.DirectorySeparatorChar, "/"c)), .Folder = r,
                                                        .Share = ShareFolder(r), .Key = "cli-" & Environment.MachineName.ToLowerInvariant() & ":" & ProjectId(r)}).ToList()
        End Function

        Public Shared Function ProjectId(root As String) As String
            Dim bytes = SHA1.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar).ToLowerInvariant()))
            Return "p" & Convert.ToHexString(bytes).Substring(0, 10).ToLowerInvariant()
        End Function

        ''' <summary>The configurations of every project, with stable, unique ids (the first project's from their names).</summary>
        Public Shared Function Configs(roots As IEnumerable(Of String)) As List(Of RunConfig)
            Dim all As New List(Of RunConfig)
            Dim ps = Projects(roots)
            For Each p In ps
                Dim list = LaunchConfigs(p.Folder)
                If list.Count = 0 Then list = DetectRunConfigs(p.Folder)
                For Each c In list.Take(12)
                    c.Project = p.Id
                    c.ProjectName = p.Name
                    all.Add(c)
                Next
            Next
            Dim seen As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
            For Each c In all
                Dim name = If(ps.Count > 0 AndAlso c.Project = ps(0).Id, c.Name, c.ProjectName & " " & c.Name)
                Dim id = Regex.Replace(name.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim("-"c)
                If id.Length = 0 Then id = "run"
                Dim unique = id, n = 2
                While Not seen.Add(unique)
                    unique = id & "-" & n
                    n += 1
                End While
                c.Id = unique
            Next
            Return all.Take(40).ToList()
        End Function

        ''' <summary>.agentic/launch.json, else .claude/launch.json: {configurations: [{name, command | runtimeExecutable + runtimeArgs, cwd, port, url, kind}]}.</summary>
        Public Shared Function LaunchConfigs(root As String) As List(Of RunConfig)
            For Each rel In {Path.Combine(".agentic", "launch.json"), Path.Combine(".claude", "launch.json")}
                Dim file = Path.Combine(root, rel)
                If Not IO.File.Exists(file) Then Continue For
                Dim list As New List(Of RunConfig)
                Try
                    Using doc = JsonDocument.Parse(IO.File.ReadAllText(file), New JsonDocumentOptions With {.CommentHandling = JsonCommentHandling.Skip, .AllowTrailingCommas = True})
                        Dim arr As JsonElement
                        If Not doc.RootElement.TryGetProperty("configurations", arr) OrElse arr.ValueKind <> JsonValueKind.Array Then Continue For
                        For Each e In arr.EnumerateArray()
                            If e.ValueKind <> JsonValueKind.Object Then Continue For
                            Dim command = JStr(e, "command")
                            Dim exe = JStr(e, "runtimeExecutable")
                            If String.IsNullOrWhiteSpace(command) AndAlso Not String.IsNullOrWhiteSpace(exe) Then
                                Dim args As New List(Of String) From {Quote(exe)}
                                Dim ra As JsonElement
                                If e.TryGetProperty("runtimeArgs", ra) AndAlso ra.ValueKind = JsonValueKind.Array Then
                                    args.AddRange(ra.EnumerateArray().Where(Function(a) a.ValueKind = JsonValueKind.String).Select(Function(a) Quote(a.GetString())))
                                End If
                                command = String.Join(" ", args)
                            End If
                            Dim port = JInt(e, "port")
                            Dim url = JStr(e, "url")
                            Dim name = If(JStr(e, "name"), If(command, "Run"))
                            Dim kind = JStr(e, "kind")
                            If String.IsNullOrWhiteSpace(kind) Then kind = If(Regex.IsMatch(name, "\btests?\b", RegexOptions.IgnoreCase), "test", If(port > 0 OrElse url IsNot Nothing, "web", "app"))
                            If String.IsNullOrWhiteSpace(command) AndAlso kind <> "static" Then Continue For
                            Dim cwd = If(JStr(e, "cwd"), ".")
                            list.Add(New RunConfig With {.Name = name, .Command = command, .Kind = kind.ToLowerInvariant(), .Port = port, .Url = url,
                                                         .Cwd = Path.GetFullPath(Path.Combine(root, If(String.IsNullOrWhiteSpace(cwd), ".", cwd))), .Source = rel.Replace("\"c, "/"c)})
                        Next
                    End Using
                Catch ex As Exception When TypeOf ex Is JsonException OrElse TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
                    Continue For
                End Try
                If list.Count > 0 Then Return list
            Next
            Return New List(Of RunConfig)
        End Function

        Private Shared Function JStr(e As JsonElement, name As String) As String
            Dim v As JsonElement
            Return If(e.TryGetProperty(name, v) AndAlso v.ValueKind = JsonValueKind.String AndAlso Not String.IsNullOrWhiteSpace(v.GetString()), v.GetString(), Nothing)
        End Function

        Private Shared Function JInt(e As JsonElement, name As String) As Integer
            Dim v As JsonElement, n As Integer
            Return If(e.TryGetProperty(name, v) AndAlso v.ValueKind = JsonValueKind.Number AndAlso v.TryGetInt32(n), n, 0)
        End Function

        Private Shared Function Quote(s As String) As String
            Return If(s Is Nothing, "", If(s.Contains(" "c) AndAlso Not s.StartsWith(""""), """" & s & """", s))
        End Function

        ''' <summary>What the project's files show: package.json scripts, a static site, Python, .NET, Go, Rust, a Makefile.</summary>
        Public Shared Function DetectRunConfigs(root As String) As List(Of RunConfig)
            Dim list As New List(Of RunConfig)
            Dim add = Sub(name As String, command As String, kind As String, port As Integer)
                          If list.Any(Function(c) String.Equals(c.Command, command, StringComparison.OrdinalIgnoreCase) AndAlso c.Kind = kind) Then Return
                          list.Add(New RunConfig With {.Name = name, .Command = command, .Kind = kind, .Port = port, .Cwd = root, .Source = "detected"})
                      End Sub
            Dim has = Function(rel As String) IO.File.Exists(Path.Combine(root, rel))
            If has("package.json") Then
                Dim pm = If(has("pnpm-lock.yaml"), "pnpm", If(has("yarn.lock"), "yarn", If(has("bun.lockb") OrElse has("bun.lock"), "bun", "npm")))
                Try
                    Using doc = JsonDocument.Parse(IO.File.ReadAllText(Path.Combine(root, "package.json")), New JsonDocumentOptions With {.AllowTrailingCommas = True})
                        Dim scripts As JsonElement
                        If doc.RootElement.TryGetProperty("scripts", scripts) AndAlso scripts.ValueKind = JsonValueKind.Object Then
                            Dim names = scripts.EnumerateObject().Select(Function(p) p.Name).ToList()
                            For Each s In {("dev", "Dev server"), ("start", "Start"), ("serve", "Serve"), ("preview", "Preview the build")}
                                If names.Contains(s.Item1) Then add(s.Item2, $"{pm} run {s.Item1}", "web", 0)
                            Next
                            If names.Contains("build") Then add("Build", $"{pm} run build", "app", 0)
                            If names.Contains("test") Then add("Tests", $"{pm} run test", "test", 0)
                        End If
                    End Using
                Catch ex As Exception When TypeOf ex Is JsonException OrElse TypeOf ex Is IOException
                End Try
            End If
            If has("index.html") AndAlso Not list.Any(Function(c) c.Kind = "web") Then
                list.Add(New RunConfig With {.Name = "Static site", .Kind = "static", .Cwd = root, .Source = "detected"})
            End If
            If has("manage.py") Then add("Django server", "python manage.py runserver", "web", 8000)
            For Each f In {"app.py", "main.py", "server.py"}
                If Not has(f) Then Continue For
                Dim code = SafeHead(Path.Combine(root, f))
                Dim stem = Path.GetFileNameWithoutExtension(f)
                If Regex.IsMatch(code, "\bfastapi\b", RegexOptions.IgnoreCase) Then
                    add("Web app", $"python -m uvicorn {stem}:app --reload", "web", 8000)
                ElseIf Regex.IsMatch(code, "\bflask\b", RegexOptions.IgnoreCase) Then
                    add("Web app", $"python {f}", "web", 5000)
                ElseIf Not list.Any(Function(c) c.Command IsNot Nothing AndAlso c.Command.StartsWith("python ")) Then
                    add("Run " & f, $"python {f}", "app", 0)
                End If
            Next
            If has("pytest.ini") OrElse has("conftest.py") OrElse (has("pyproject.toml") AndAlso SafeHead(Path.Combine(root, "pyproject.toml")).Contains("pytest")) OrElse
               (Directory.Exists(Path.Combine(root, "tests")) AndAlso Directory.EnumerateFiles(Path.Combine(root, "tests"), "test_*.py").Any()) Then
                add("Tests", "python -m pytest", "test", 0)
            End If
            Dim testAdded = False
            For Each p In SafeFiles(root, {"*.csproj", "*.vbproj", "*.fsproj"}, 3)
                Dim xml = SafeHead(p)
                Dim rel = Path.GetRelativePath(root, p)
                Dim nice = Path.GetFileNameWithoutExtension(p)
                If Regex.IsMatch(xml, "Microsoft\.NET\.Test\.Sdk|xunit|nunit|MSTest", RegexOptions.IgnoreCase) Then
                    If Not testAdded Then add("Tests", "dotnet test", "test", 0)
                    testAdded = True
                ElseIf xml.Contains("Microsoft.NET.Sdk.Web") Then
                    add($"Web app ({nice})", $"dotnet run --project ""{rel}""", "web", 0)
                ElseIf Regex.IsMatch(xml, "<OutputType>\s*(Exe|WinExe)\s*</OutputType>", RegexOptions.IgnoreCase) Then
                    add($"Run {nice}", $"dotnet run --project ""{rel}""", "app", 0)
                End If
            Next
            If has("go.mod") Then
                add("Run", "go run .", "app", 0)
                add("Tests", "go test ./...", "test", 0)
            End If
            If has("Cargo.toml") Then
                add("Run", "cargo run", "app", 0)
                add("Tests", "cargo test", "test", 0)
            End If
            If has("Makefile") Then
                For Each m As Match In Regex.Matches(SafeHead(Path.Combine(root, "Makefile")), "^(run|dev|start|serve|test):", RegexOptions.Multiline)
                    Dim t = m.Groups(1).Value
                    add("make " & t, "make " & t, If(t = "test", "test", If(t = "run", "app", "web")), 0)
                Next
            End If
            Return list
        End Function

        Private Shared Function SafeHead(file As String) As String
            Try
                Using r As New StreamReader(file)
                    Dim buf(200000) As Char
                    Dim n = r.ReadBlock(buf, 0, buf.Length)
                    Return New String(buf, 0, n)
                End Using
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
                Return ""
            End Try
        End Function

        Private Shared Function SafeFiles(root As String, patterns As String(), depth As Integer) As List(Of String)
            Dim found As New List(Of String)
            Dim skip = New HashSet(Of String)(StringComparer.OrdinalIgnoreCase) From {"node_modules", "bin", "obj", "dist", "build", "target", "venv", "packages", "AppData", "Library"}
            Dim walk As Action(Of String, Integer) = Nothing
            walk = Sub(dir As String, level As Integer)
                       If found.Count > 40 Then Return
                       Try
                           For Each p In patterns
                               found.AddRange(Directory.EnumerateFiles(dir, p))
                           Next
                           If level >= depth Then Return
                           For Each d In Directory.EnumerateDirectories(dir)
                               Dim name = Path.GetFileName(d)
                               If Not name.StartsWith("."c) AndAlso Not skip.Contains(name) Then walk(d, level + 1)
                           Next
                       Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
                       End Try
                   End Sub
            walk(root, 1)
            Return found
        End Function

        ''' <summary>What Share uploads: the build (dist, build, out, _site, public, www) with index.html, else a static site's own folder.</summary>
        Public Shared Function ShareFolder(root As String) As String
            For Each d In {"dist", "build", "out", "_site", "public", "www"}
                If IO.File.Exists(Path.Combine(root, d, "index.html")) Then Return d
            Next
            If Not IO.File.Exists(Path.Combine(root, "index.html")) Then Return Nothing
            If IO.File.Exists(Path.Combine(root, "package.json")) AndAlso SafeHead(Path.Combine(root, "package.json")).Contains("""build""") Then Return Nothing
            Return "."
        End Function

        ''' <summary>.agentic/launch.json, written from what was detected when it is not there yet; returns its path.</summary>
        Public Shared Function EnsureLaunchFile(root As String) As String
            Dim file = Path.Combine(root, ".agentic", "launch.json")
            If IO.File.Exists(file) Then Return file
            Directory.CreateDirectory(Path.GetDirectoryName(file))
            Dim configs As New JsonArray()
            For Each c In DetectRunConfigs(root).Where(Function(x) x.Command IsNot Nothing)
                Dim o As New JsonObject From {{"name", c.Name}, {"command", c.Command}, {"cwd", "."}, {"kind", c.Kind}}
                If c.Port > 0 Then o("port") = c.Port
                configs.Add(o)
            Next
            Dim doc As New JsonObject From {{"version", "0.0.1"}, {"configurations", configs}}
            IO.File.WriteAllText(file, doc.ToJsonString(New JsonSerializerOptions With {.WriteIndented = True}) & vbLf)
            Return file
        End Function

        ' ------------------------------------------------------------------ runs

        ''' <summary>Starts a configuration (stopping it first when it runs).</summary>
        Public Function Start(cfg As RunConfig) As ProjectRun
            [Stop](cfg.Id)
            Dim run As New ProjectRun With {.Config = cfg, .Started = DateTime.UtcNow}
            If cfg.Kind = "static" Then
                run.Site = New StaticSite(cfg.Cwd)
                run.Url = run.Site.Start()
            Else
                ' Dev servers must not open a browser of their own; a configured port is the one to use.
                Dim env As New Dictionary(Of String, String) From {{"BROWSER", "none"}, {"FORCE_COLOR", "0"}}
                If cfg.Port > 0 Then env("PORT") = cfg.Port.ToString(Globalization.CultureInfo.InvariantCulture)
                run.Shell = _shells.StartBackground(cfg.Command, cfg.Cwd, env)
                If cfg.Kind = "web" AndAlso (cfg.Url IsNot Nothing OrElse cfg.Port > 0) Then
                    Dim unused = Task.Run(Function() WaitForPortAsync(run))
                End If
            End If
            SyncLock _runs
                _runs(cfg.Id) = run
            End SyncLock
            Return run
        End Function

        Private Shared Async Function WaitForPortAsync(run As ProjectRun) As Task
            Dim u As Uri = Nothing
            If Not Uri.TryCreate(If(run.Config.Url, $"http://localhost:{run.Config.Port}/"), UriKind.Absolute, u) Then Return
            For i = 1 To 240
                If run.Url IsNot Nothing OrElse Not run.Running Then Return
                Try
                    Using c As New TcpClient()
                        Await c.ConnectAsync(If(u.Host = "localhost", "127.0.0.1", u.Host), u.Port).WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(False)
                        If c.Connected Then
                            run.Url = u.ToString()
                            Return
                        End If
                    End Using
                Catch ex As Exception When TypeOf ex Is SocketException OrElse TypeOf ex Is TimeoutException OrElse TypeOf ex Is ObjectDisposedException
                End Try
                Await Task.Delay(500).ConfigureAwait(False)
            Next
        End Function

        ''' <summary>A run's output from a position (colour codes and the shell's bookkeeping line left out), and where it ends.</summary>
        Public Shared Function OutputFrom(run As ProjectRun, from As Integer) As (Text As String, Pos As Integer)
            If run.Shell Is Nothing Then Return (If(from = 0 AndAlso run.Url IsNot Nothing, $"Serving {run.Config.Cwd} at {run.Url}" & vbLf, ""), If(run.Url Is Nothing, 0, 1))
            Dim raw As String
            Dim pos As Integer
            SyncLock run.Shell.Output
                Dim start = Math.Max(0, Math.Min(from, run.Shell.Output.Length))
                raw = run.Shell.Output.ToString(start, run.Shell.Output.Length - start)
                pos = run.Shell.Output.Length
            End SyncLock
            Return (Clean(raw), pos)
        End Function

        Private Shared Function Clean(raw As String) As String
            Return Regex.Replace(AnsiCodes.Replace(raw, ""), "(?m)^__AGENTIC_CWD__=.*(\r?\n)?", "")
        End Function

        ''' <summary>Finds a web app's address in its output (once), in whole lines (the last one may still be coming).</summary>
        Public Shared Sub ScanForUrl(run As ProjectRun)
            If run.Url IsNot Nothing OrElse run.Shell Is Nothing OrElse (run.Config.Kind <> "web" AndAlso run.Config.Kind <> "app") Then Return
            Dim raw As String
            SyncLock run.Shell.Output
                Dim start = Math.Min(run.Scanned, run.Shell.Output.Length)
                raw = run.Shell.Output.ToString(start, run.Shell.Output.Length - start)
            End SyncLock
            Dim lastBreak = raw.LastIndexOf(ChrW(10))
            If lastBreak < 0 Then Return
            Dim m = UrlPattern.Match(Clean(raw.Substring(0, lastBreak)))
            If m.Success Then run.Url = Regex.Replace(m.Value.TrimEnd("."c, ","c), "//(0\.0\.0\.0|\[::\])", "//localhost")
            run.Scanned += lastBreak + 1
        End Sub

        Public Function [Get](id As String) As ProjectRun
            SyncLock _runs
                Dim r As ProjectRun = Nothing
                Return If(_runs.TryGetValue(id, r), r, Nothing)
            End SyncLock
        End Function

        Public Function All() As List(Of ProjectRun)
            SyncLock _runs
                Dim list = _runs.Values.OrderBy(Function(r) r.Started).ToList()
                For Each r In list
                    ScanForUrl(r)
                Next
                Return list
            End SyncLock
        End Function

        Public Function [Stop](id As String) As Boolean
            Dim run = [Get](id)
            If run Is Nothing OrElse Not run.Running Then Return False
            run.Stopped = True
            run.Site?.Dispose()
            If run.Shell IsNot Nothing Then _shells.Kill(run.Shell.Id)
            Return True
        End Function

        Public Function StopAll() As Integer
            Return All().Where(Function(r) [Stop](r.Config.Id)).Count()
        End Function

        ' ------------------------------------------------------------------ sharing

        ''' <summary>A folder as a zip (no node_modules, .git or hidden files and folders); refused when it is larger than maxBytes.</summary>
        Public Shared Function ZipSite(folder As String, maxBytes As Long) As Byte()
            Dim skip = New HashSet(Of String)(StringComparer.OrdinalIgnoreCase) From {"node_modules", "__pycache__", "bin", "obj"}
            Dim total As Long = 0, count = 0
            Using ms As New MemoryStream()
                Using archive As New ZipArchive(ms, ZipArchiveMode.Create, leaveOpen:=True)
                    Dim walk As Action(Of String) = Nothing
                    walk = Sub(dir As String)
                               For Each f In Directory.EnumerateFiles(dir)
                                   If Path.GetFileName(f).StartsWith("."c) Then Continue For
                                   total += New FileInfo(f).Length
                                   count += 1
                                   If total > maxBytes Then Throw New InvalidOperationException($"The website is larger than {maxBytes \ (1024 * 1024)} MB.")
                                   If count > 3000 Then Throw New InvalidOperationException("The website has more than 3000 files.")
                                   archive.CreateEntryFromFile(f, Path.GetRelativePath(folder, f).Replace(Path.DirectorySeparatorChar, "/"c), CompressionLevel.Fastest)
                               Next
                               For Each d In Directory.EnumerateDirectories(dir)
                                   Dim name = Path.GetFileName(d)
                                   If Not name.StartsWith("."c) AndAlso Not skip.Contains(name) Then walk(d)
                               Next
                           End Sub
                    walk(folder)
                End Using
                Return ms.ToArray()
            End Using
        End Function

    End Class

    ''' <summary>A small web server for a static site: one folder on 127.0.0.1, the right content types, no caching, nothing outside it.</summary>
    Friend NotInheritable Class StaticSite
        Implements IDisposable

        Private ReadOnly _root As String
        Private _listener As HttpListener
        Private ReadOnly _cts As New CancellationTokenSource()
        Private Shared ReadOnly Types As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase) From {
            {".html", "text/html; charset=utf-8"}, {".htm", "text/html; charset=utf-8"}, {".js", "text/javascript; charset=utf-8"}, {".mjs", "text/javascript; charset=utf-8"},
            {".css", "text/css; charset=utf-8"}, {".json", "application/json; charset=utf-8"}, {".map", "application/json"}, {".txt", "text/plain; charset=utf-8"},
            {".svg", "image/svg+xml"}, {".png", "image/png"}, {".jpg", "image/jpeg"}, {".jpeg", "image/jpeg"}, {".gif", "image/gif"}, {".webp", "image/webp"},
            {".ico", "image/x-icon"}, {".wav", "audio/wav"}, {".mp3", "audio/mpeg"}, {".ogg", "audio/ogg"}, {".mp4", "video/mp4"}, {".webm", "video/webm"},
            {".woff", "font/woff"}, {".woff2", "font/woff2"}, {".ttf", "font/ttf"}, {".wasm", "application/wasm"}, {".xml", "application/xml"}, {".pdf", "application/pdf"}}

        Public Sub New(root As String)
            _root = Path.GetFullPath(root)
        End Sub

        Public Function Start() As String
            Dim port As Integer
            Using s As New Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
                s.Bind(New IPEndPoint(IPAddress.Loopback, 0))
                port = DirectCast(s.LocalEndPoint, IPEndPoint).Port
            End Using
            _listener = New HttpListener()
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/")
            _listener.Start()
            Dim unused = Task.Run(AddressOf ServeAsync)
            Return $"http://127.0.0.1:{port}/"
        End Function

        Private Async Function ServeAsync() As Task
            While Not _cts.IsCancellationRequested
                Dim ctx As HttpListenerContext
                Try
                    ctx = Await _listener.GetContextAsync().ConfigureAwait(False)
                Catch ex As Exception When TypeOf ex Is HttpListenerException OrElse TypeOf ex Is ObjectDisposedException OrElse TypeOf ex Is InvalidOperationException
                    Return
                End Try
                Dim unused = Task.Run(Function() HandleAsync(ctx))
            End While
        End Function

        Private Async Function HandleAsync(ctx As HttpListenerContext) As Task
            Dim res = ctx.Response
            Try
                Dim rel = Uri.UnescapeDataString(ctx.Request.Url.AbsolutePath).TrimStart("/"c).Replace("/"c, Path.DirectorySeparatorChar)
                Dim full = Path.GetFullPath(Path.Combine(_root, rel))
                Dim inside = full.Equals(_root, StringComparison.OrdinalIgnoreCase) OrElse full.StartsWith(_root.TrimEnd(Path.DirectorySeparatorChar) & Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                If inside AndAlso Directory.Exists(full) Then full = Path.Combine(full, "index.html")
                If Not inside OrElse Not File.Exists(full) Then
                    res.StatusCode = 404
                    Await res.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("Not found")).ConfigureAwait(False)
                    Return
                End If
                Dim type As String = Nothing
                res.ContentType = If(Types.TryGetValue(Path.GetExtension(full), type), type, "application/octet-stream")
                res.Headers("Cache-Control") = "no-store"
                Dim bytes = Await File.ReadAllBytesAsync(full).ConfigureAwait(False)
                res.ContentLength64 = bytes.Length
                If ctx.Request.HttpMethod <> "HEAD" Then Await res.OutputStream.WriteAsync(bytes).ConfigureAwait(False)
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is HttpListenerException OrElse TypeOf ex Is UnauthorizedAccessException OrElse TypeOf ex Is ArgumentException
                Try
                    res.StatusCode = 500
                Catch inner As InvalidOperationException
                End Try
            Finally
                Try
                    res.Close()
                Catch ex As Exception When TypeOf ex Is HttpListenerException OrElse TypeOf ex Is ObjectDisposedException
                End Try
            End Try
        End Function

        Public Sub Dispose() Implements IDisposable.Dispose
            _cts.Cancel()
            Try
                _listener?.Stop()
                _listener?.Close()
            Catch ex As ObjectDisposedException
            End Try
        End Sub
    End Class

End Namespace
