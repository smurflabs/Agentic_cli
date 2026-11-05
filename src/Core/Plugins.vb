Option Strict On
Option Explicit On
Option Infer On

Imports System.Diagnostics
Imports System.IO
Imports System.Net.Http
Imports System.Text.Json
Imports System.Text.Json.Nodes
Imports System.Text.RegularExpressions

Namespace Core

    Public Class PluginCounts
        Public Property Commands As Integer
        Public Property Agents As Integer
        Public Property Skills As Integer
        Public Property Hooks As Integer
        Public Property Mcp As Integer
    End Class

    Public Class PluginInfo
        Public Property Id As String
        Public Property Name As String
        Public Property Marketplace As String
        Public Property Version As String
        Public Property Description As String
        Public Property Dir As String
        ''' <summary>"Claude Code" (its registry) or "Agentic" (installed with /plugin).</summary>
        Public Property Source As String
        Public Property Enabled As Boolean
        Public Property Removable As Boolean
        ''' <summary>Its hooks run: installed with Agentic, or a Claude Code plugin turned on in /plugin (hooks are written for Claude Code).</summary>
        Public Property HooksOn As Boolean
        Public Property Counts As New PluginCounts()
    End Class

    Public Class MarketPlugin
        Public Property Name As String
        Public Property Description As String
        Public Property Version As String
    End Class

    Public Class MarketInfo
        Public Property Name As String
        Public Property Source As String
        Public Property Dir As String
        Public Property Removable As Boolean
        Public Property Plugins As New List(Of MarketPlugin)
    End Class

    ''' <summary>
    ''' Claude Code plugins in Agentic: what an installed plugin brings (slash commands, subagents, skills, hooks and MCP
    ''' servers) works here, and /plugin lists, switches, installs and removes them.
    ''' Installed plugins come from Claude Code (~\.claude\plugins\installed_plugins.json) and from Agentic's own folder
    ''' (~\.agentic\plugins\installed_plugins.json, the same format). On or off: Agentic's choice (~\.agentic\plugins\
    ''' settings.json enabledPlugins) wins, else Claude Code's (~\.claude\settings.json enabledPlugins); switching one here
    ''' never changes Claude Code's files. A marketplace is a folder or git repository with .claude-plugin\marketplace.json;
    ''' installing copies (or clones) a plugin to ~\.agentic\plugins\cache\&lt;marketplace&gt;\&lt;plugin&gt;\&lt;version&gt;.
    ''' (The desktop app and VS Code keep the same files.)
    ''' </summary>
    Public NotInheritable Class Plugins

        Private Shared Function HomeDir() As String
            Return Paths.Home()
        End Function

        Public Shared Function AgenticDir() As String
            Dim custom = Environment.GetEnvironmentVariable("AGENTIC_PLUGINS_DIR")
            Return If(String.IsNullOrWhiteSpace(custom), Path.Combine(HomeDir(), ".agentic", "plugins"), custom)
        End Function

        Private Shared Function ClaudeDir() As String
            Return Path.Combine(HomeDir(), ".claude", "plugins")
        End Function

        Private Shared ReadOnly DocOptions As New JsonDocumentOptions With {.AllowTrailingCommas = True, .CommentHandling = JsonCommentHandling.Skip}

        Private Shared Function ReadNode(file As String) As JsonNode
            Try
                If Not IO.File.Exists(file) Then Return Nothing
                Return JsonNode.Parse(IO.File.ReadAllText(file), Nothing, DocOptions)
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is JsonException OrElse TypeOf ex Is UnauthorizedAccessException
                Return Nothing
            End Try
        End Function

        Private Shared Sub WriteNode(file As String, node As JsonNode)
            Directory.CreateDirectory(Path.GetDirectoryName(file))
            IO.File.WriteAllText(file, node.ToJsonString(New JsonSerializerOptions With {.WriteIndented = True}) & vbLf)
        End Sub

        Private Shared Function Str(n As JsonNode, key As String) As String
            Try
                Dim v = TryCast(n, JsonObject)?(key)
                If v Is Nothing Then Return ""
                Return If(TypeOf v Is JsonValue, v.ToString(), "")
            Catch ex As InvalidOperationException
                Return ""
            End Try
        End Function

        Private Shared Function SamePath(a As String, b As String) As Boolean
            If String.IsNullOrEmpty(a) OrElse String.IsNullOrEmpty(b) Then Return False
            Dim n = Function(p As String) Path.GetFullPath(p).TrimEnd("\"c, "/"c)
            Return String.Equals(n(a), n(b), If(OperatingSystem.IsWindows(), StringComparison.OrdinalIgnoreCase, StringComparison.Ordinal))
        End Function

        Private Shared Function EnabledMap(file As String) As Dictionary(Of String, Boolean)
            Dim map As New Dictionary(Of String, Boolean)(StringComparer.Ordinal)
            Dim ep = TryCast(TryCast(ReadNode(file), JsonObject)?("enabledPlugins"), JsonObject)
            If ep Is Nothing Then Return map
            For Each kv In ep
                Dim v = TryCast(kv.Value, JsonValue)
                Dim b As Boolean
                If v IsNot Nothing AndAlso v.TryGetValue(b) Then map(kv.Key) = b
            Next
            Return map
        End Function

        Private Shared Function AgenticEnabled() As Dictionary(Of String, Boolean)
            Return EnabledMap(Path.Combine(AgenticDir(), "settings.json"))
        End Function

        Private Shared Function IsEnabled(id As String, own As Dictionary(Of String, Boolean), claude As Dictionary(Of String, Boolean)) As Boolean
            Dim v As Boolean
            If own.TryGetValue(id, v) Then Return v
            Return Not (claude.TryGetValue(id, v) AndAlso Not v)
        End Function

        ''' <summary>A plugin's manifest (.claude-plugin\plugin.json); an empty object when it has none.</summary>
        Public Shared Function Manifest(dir As String) As JsonObject
            Return If(TryCast(ReadNode(Path.Combine(dir, ".claude-plugin", "plugin.json")), JsonObject), New JsonObject())
        End Function

        Private Shared Function ListOf(n As JsonNode) As List(Of String)
            Dim out As New List(Of String)
            If TypeOf n Is JsonValue Then
                Dim s As String = Nothing
                If DirectCast(n, JsonValue).TryGetValue(s) Then out.Add(s)
            ElseIf TypeOf n Is JsonArray Then
                For Each x In DirectCast(n, JsonArray)
                    Dim s As String = Nothing
                    If TypeOf x Is JsonValue AndAlso DirectCast(x, JsonValue).TryGetValue(s) Then out.Add(s)
                Next
            End If
            Return out
        End Function

        ''' <summary>The folders of a kind (commands, agents, skills) in a plugin: the default one and those its manifest adds.</summary>
        Public Shared Function Folders(dir As String, kind As String) As List(Of String)
            Dim list As New List(Of String) From {Path.Combine(dir, kind)}
            For Each p In ListOf(Manifest(dir)(kind))
                list.Add(Path.GetFullPath(Path.Combine(dir, p)))
            Next
            Dim out As New List(Of String)
            For Each p In list
                If Directory.Exists(p) AndAlso Not out.Any(Function(q) SamePath(p, q)) Then out.Add(p)
            Next
            Return out
        End Function

        ''' <summary>The plugin's hooks {Event: [groups]} (hooks\hooks.json or its manifest's), with ${CLAUDE_PLUGIN_ROOT} as its folder.</summary>
        Public Shared Function HooksOf(dir As String) As JsonObject
            Dim m = Manifest(dir)
            Dim doc As JsonNode = Nothing
            Dim h = m("hooks")
            If TypeOf h Is JsonValue Then
                doc = ReadNode(Path.GetFullPath(Path.Combine(dir, h.ToString())))
            ElseIf TypeOf h Is JsonObject Then
                doc = If(h("hooks") IsNot Nothing, h, New JsonObject From {{"hooks", h.DeepClone()}})
            End If
            If doc Is Nothing Then doc = ReadNode(Path.Combine(dir, "hooks", "hooks.json"))
            Dim hooks = TryCast(TryCast(doc, JsonObject)?("hooks"), JsonObject)
            If hooks Is Nothing Then Return New JsonObject()
            Dim text = hooks.ToJsonString().Replace("${CLAUDE_PLUGIN_ROOT}", JsonEncodedText.Encode(dir).ToString())
            Return If(TryCast(JsonNode.Parse(text), JsonObject), New JsonObject())
        End Function

        Private Shared Function CountMd(dirs As List(Of String)) As Integer
            Dim n = 0
            For Each d In dirs
                Try
                    n += Directory.EnumerateFiles(d, "*.md", SearchOption.AllDirectories).Count()
                Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
                End Try
            Next
            Return n
        End Function

        Private Shared Function CountsOf(dir As String) As PluginCounts
            Dim skills = 0
            For Each d In Folders(dir, "skills")
                Try
                    skills += Directory.EnumerateDirectories(d).Count(Function(x) IO.File.Exists(Path.Combine(x, "SKILL.md")))
                Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
                End Try
            Next
            Dim mcpDoc = TryCast(ReadNode(Path.Combine(dir, ".mcp.json")), JsonObject)
            Dim servers = If(TryCast(mcpDoc?("mcpServers"), JsonObject), mcpDoc)
            Dim mcp = If(servers?.Count, 0) + If(TryCast(Manifest(dir)("mcpServers"), JsonObject)?.Count, 0)
            Return New PluginCounts With {.Commands = CountMd(Folders(dir, "commands")), .Agents = CountMd(Folders(dir, "agents")), .Skills = skills,
                                          .Hooks = HooksOf(dir).Sum(Function(kv) If(TryCast(kv.Value, JsonArray)?.Count, 0)), .Mcp = mcp}
        End Function

        Private Shared Function Registry(file As String, source As String, projectDirs As IEnumerable(Of String),
                                         own As Dictionary(Of String, Boolean), claude As Dictionary(Of String, Boolean), withCounts As Boolean) As List(Of PluginInfo)
            Dim out As New List(Of PluginInfo)
            Dim plugins = TryCast(TryCast(ReadNode(file), JsonObject)?("plugins"), JsonObject)
            If plugins Is Nothing Then Return out
            For Each kv In plugins
                Dim entries = If(TypeOf kv.Value Is JsonArray, DirectCast(kv.Value, JsonArray).ToList(), New List(Of JsonNode) From {kv.Value})
                For Each e In entries
                    Dim dir = Str(e, "installPath")
                    If dir.Length = 0 OrElse Not Directory.Exists(dir) Then Continue For
                    Dim scope = Str(e, "scope")
                    If (scope = "project" OrElse scope = "local") AndAlso Not projectDirs.Any(Function(r) SamePath(r, Str(e, "projectPath"))) Then Continue For
                    Dim m = Manifest(dir)
                    Dim parts = kv.Key.Split("@"c)
                    Dim isOn = IsEnabled(kv.Key, own, claude)
                    Dim explicitlyOn As Boolean
                    out.Add(New PluginInfo With {
                        .Id = kv.Key, .Name = If(Str(m, "name").Length > 0, Str(m, "name"), parts(0)), .Marketplace = If(parts.Length > 1, parts(1), ""),
                        .Version = If(Str(e, "version").Length > 0, Str(e, "version"), Str(m, "version")), .Description = Str(m, "description"), .Dir = dir,
                        .Source = source, .Enabled = isOn, .Removable = source = "Agentic",
                        .HooksOn = isOn AndAlso (source = "Agentic" OrElse (own.TryGetValue(kv.Key, explicitlyOn) AndAlso explicitlyOn)),
                        .Counts = If(withCounts, CountsOf(dir), New PluginCounts())})
                    Exit For
                Next
            Next
            Return out
        End Function

        ''' <summary>Every installed plugin (Agentic's own first: the same id installed in both is Agentic's).</summary>
        Public Shared Function Installed(Optional projectDirs As IEnumerable(Of String) = Nothing, Optional withCounts As Boolean = True) As List(Of PluginInfo)
            Dim dirs = If(projectDirs, Enumerable.Empty(Of String)()).ToList()
            Dim own = AgenticEnabled()
            Dim claude = EnabledMap(Path.Combine(HomeDir(), ".claude", "settings.json"))
            Dim list = Registry(Path.Combine(AgenticDir(), "installed_plugins.json"), "Agentic", dirs, own, claude, withCounts)
            For Each p In Registry(Path.Combine(ClaudeDir(), "installed_plugins.json"), "Claude Code", dirs, own, claude, withCounts)
                If Not list.Any(Function(x) x.Id = p.Id) Then list.Add(p)
            Next
            Return list
        End Function

        ''' <summary>The plugins that are on (what the chat loads).</summary>
        Public Shared Function Enabled(Optional projectDirs As IEnumerable(Of String) = Nothing) As List(Of PluginInfo)
            Return Installed(projectDirs, False).Where(Function(p) p.Enabled).ToList()
        End Function

        Public Shared Sub SetEnabled(id As String, isOn As Boolean)
            If String.IsNullOrWhiteSpace(id) Then Throw New ArgumentException("Which plugin? Give its id (name@marketplace).")
            Dim file = Path.Combine(AgenticDir(), "settings.json")
            Dim doc = If(TryCast(ReadNode(file), JsonObject), New JsonObject())
            Dim ep = TryCast(doc("enabledPlugins"), JsonObject)
            If ep Is Nothing Then
                ep = New JsonObject()
                doc("enabledPlugins") = ep
            End If
            ep(id) = isOn
            WriteNode(file, doc)
        End Sub

        ' ------------------------------------------------------------------ marketplaces

        Private Shared Function MarketDoc(dir As String) As JsonObject
            Return If(TryCast(ReadNode(Path.Combine(dir, ".claude-plugin", "marketplace.json")), JsonObject), TryCast(ReadNode(Path.Combine(dir, "marketplace.json")), JsonObject))
        End Function

        Private Shared Function SourceText(n As JsonNode) As String
            If n Is Nothing Then Return ""
            If TypeOf n Is JsonValue Then Return n.ToString()
            For Each k In {"repo", "url", "path", "source"}
                Dim v = Str(n, k)
                If v.Length > 0 Then Return If(k = "repo", "github:" & v, v)
            Next
            Return ""
        End Function

        Public Shared Function Marketplaces() As List(Of MarketInfo)
            Dim out As New List(Of MarketInfo)
            For Each pair In {(Path.Combine(AgenticDir(), "known_marketplaces.json"), True), (Path.Combine(ClaudeDir(), "known_marketplaces.json"), False)}
                Dim known = TryCast(ReadNode(pair.Item1), JsonObject)
                If known Is Nothing Then Continue For
                For Each kv In known
                    Dim dir = Str(kv.Value, "installLocation")
                    If dir.Length = 0 Then dir = Str(kv.Value, "path")
                    If dir.Length = 0 OrElse out.Any(Function(x) x.Name = kv.Key) Then Continue For
                    Dim info As New MarketInfo With {.Name = kv.Key, .Source = SourceText(TryCast(kv.Value, JsonObject)?("source")), .Dir = dir, .Removable = pair.Item2}
                    Dim plugins = TryCast(MarketDoc(dir)?("plugins"), JsonArray)
                    If plugins IsNot Nothing Then
                        For Each p In plugins
                            If Str(p, "name").Length > 0 Then info.Plugins.Add(New MarketPlugin With {.Name = Str(p, "name"), .Description = Str(p, "description"), .Version = Str(p, "version")})
                        Next
                    End If
                    out.Add(info)
                Next
            Next
            Return out
        End Function

        Private Shared Function RunGit(args As IEnumerable(Of String), Optional cwd As String = Nothing) As String
            Dim psi As New ProcessStartInfo("git") With {.RedirectStandardOutput = True, .RedirectStandardError = True, .UseShellExecute = False, .CreateNoWindow = True}
            For Each a In args
                psi.ArgumentList.Add(a)
            Next
            If cwd IsNot Nothing Then psi.WorkingDirectory = cwd
            psi.Environment("GIT_TERMINAL_PROMPT") = "0"
            Using p = Process.Start(psi)
                Dim errTask = p.StandardError.ReadToEndAsync()
                Dim outText = p.StandardOutput.ReadToEnd()
                If Not p.WaitForExit(180000) Then
                    Try
                        p.Kill(True)
                    Catch ex As InvalidOperationException
                    End Try
                    Throw New IOException("git " & args.First() & " took too long.")
                End If
                If p.ExitCode <> 0 Then Throw New IOException("git " & args.First() & " failed: " & errTask.Result.Trim())
                Return outText
            End Using
        End Function

        ''' <summary>A marketplace source as git can clone it (owner/repo is GitHub); Nothing for a folder or a marketplace.json URL.</summary>
        Public Shared Function GitUrlOf(source As String) As String
            Dim s = If(source, "").Trim()
            If Regex.IsMatch(s, "^[\w.-]+/[\w.-]+$") AndAlso Not Directory.Exists(s) Then Return "https://github.com/" & Regex.Replace(s, "\.git$", "") & ".git"
            If s.StartsWith("github:", StringComparison.OrdinalIgnoreCase) Then Return "https://github.com/" & Regex.Replace(s.Substring(7), "\.git$", "") & ".git"
            If Regex.IsMatch(s, "^(https?|git|ssh)://", RegexOptions.IgnoreCase) AndAlso Not Regex.IsMatch(s, "\.json(\?|$)", RegexOptions.IgnoreCase) Then Return s
            If s.StartsWith("git@", StringComparison.Ordinal) Then Return s
            Return Nothing
        End Function

        Private Shared Function SafeName(s As String) As String
            Dim t = Regex.Replace(If(s, ""), "[^\w.-]+", "-").Trim("-"c)
            If t.Length > 64 Then t = t.Substring(0, 64)
            Return If(t.Length = 0, "marketplace", t)
        End Function

        ''' <summary>Adds a marketplace: owner/repo, a git URL, a URL of a marketplace.json, or a local folder. Returns its name.</summary>
        Public Shared Async Function AddMarketplaceAsync(source As String) As Task(Of String)
            Dim src = If(source, "").Trim()
            If src.Length = 0 Then Throw New ArgumentException("Say where the marketplace is: owner/repo, a git URL or a folder.")
            Dim root = Path.Combine(AgenticDir(), "marketplaces")
            Directory.CreateDirectory(root)
            Dim dir As String
            Dim recorded As JsonObject
            Dim git = GitUrlOf(src)
            If Directory.Exists(src) Then
                dir = Path.GetFullPath(src)
                recorded = New JsonObject From {{"source", "directory"}, {"path", dir}}
            ElseIf git IsNot Nothing Then
                Dim tmp = Path.Combine(root, ".new-" & DateTime.UtcNow.Ticks.ToString())
                Await Task.Run(Sub() RunGit({"clone", "--depth", "1", git, tmp})).ConfigureAwait(False)
                Dim doc = MarketDoc(tmp)
                If doc Is Nothing Then
                    Directory.Delete(tmp, True)
                    Throw New InvalidOperationException("That repository has no .claude-plugin/marketplace.json.")
                End If
                dir = Path.Combine(root, SafeName(If(Str(doc, "name").Length > 0, Str(doc, "name"), Path.GetFileNameWithoutExtension(git))))
                If Directory.Exists(dir) Then DeleteTree(dir)
                Directory.Move(tmp, dir)
                Dim gh = Regex.Match(git, "github\.com[/:]([^/]+/[^/.]+)")
                recorded = If(gh.Success, New JsonObject From {{"source", "github"}, {"repo", gh.Groups(1).Value}}, New JsonObject From {{"source", "git"}, {"url", git}})
            ElseIf Regex.IsMatch(src, "^https?://", RegexOptions.IgnoreCase) Then
                Using http As New HttpClient With {.Timeout = TimeSpan.FromSeconds(60)}
                    Dim text = Await http.GetStringAsync(src).ConfigureAwait(False)
                    Dim doc = TryCast(JsonNode.Parse(text, Nothing, DocOptions), JsonObject)
                    If doc Is Nothing Then Throw New InvalidOperationException("That URL is not a marketplace.json.")
                    dir = Path.Combine(root, SafeName(Str(doc, "name")))
                    WriteNode(Path.Combine(dir, ".claude-plugin", "marketplace.json"), doc)
                End Using
                recorded = New JsonObject From {{"source", "url"}, {"url", src}}
            Else
                Throw New ArgumentException(src & " is not a folder, owner/repo or URL.")
            End If
            Dim mdoc = MarketDoc(dir)
            If mdoc Is Nothing Then Throw New InvalidOperationException("No .claude-plugin/marketplace.json found there.")
            Dim name = SafeName(If(Str(mdoc, "name").Length > 0, Str(mdoc, "name"), Path.GetFileName(dir)))
            Dim file = Path.Combine(AgenticDir(), "known_marketplaces.json")
            Dim known = If(TryCast(ReadNode(file), JsonObject), New JsonObject())
            known(name) = New JsonObject From {{"source", recorded}, {"installLocation", dir}, {"lastUpdated", DateTime.UtcNow.ToString("o")}}
            WriteNode(file, known)
            Return name
        End Function

        Public Shared Sub RemoveMarketplace(name As String)
            Dim file = Path.Combine(AgenticDir(), "known_marketplaces.json")
            Dim known = If(TryCast(ReadNode(file), JsonObject), New JsonObject())
            Dim m = known(If(name, ""))
            If m Is Nothing Then Throw New ArgumentException($"No marketplace {name} in Agentic's list (Claude Code's are removed with Claude Code).")
            Dim dir = Str(m, "installLocation")
            known.Remove(name)
            WriteNode(file, known)
            If dir.Length > 0 AndAlso SamePath(Path.GetDirectoryName(dir), Path.Combine(AgenticDir(), "marketplaces")) AndAlso Directory.Exists(dir) Then DeleteTree(dir)
        End Sub

        Public Shared Async Function UpdateMarketplaceAsync(name As String) As Task
            Dim m = Marketplaces().FirstOrDefault(Function(x) x.Name = name)
            If m Is Nothing Then Throw New ArgumentException("No marketplace " & name & ".")
            If Directory.Exists(Path.Combine(m.Dir, ".git")) Then Await Task.Run(Sub() RunGit({"pull", "--ff-only"}, m.Dir)).ConfigureAwait(False)
        End Function

        Private Shared Sub CopyTree(from As String, target As String)
            Directory.CreateDirectory(target)
            For Each f In Directory.EnumerateFiles(from)
                IO.File.Copy(f, Path.Combine(target, Path.GetFileName(f)), True)
            Next
            For Each d In Directory.EnumerateDirectories(from)
                Dim n = Path.GetFileName(d)
                If n = ".git" OrElse n = "node_modules" Then Continue For
                CopyTree(d, Path.Combine(target, n))
            Next
        End Sub

        ''' <summary>Deletes a folder, read-only files too (git's pack files are read-only on Windows).</summary>
        Private Shared Sub DeleteTree(dir As String)
            If Not Directory.Exists(dir) Then Return
            For Each f In Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                Try
                    IO.File.SetAttributes(f, FileAttributes.Normal)
                Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
                End Try
            Next
            Directory.Delete(dir, True)
        End Sub

        ''' <summary>Installs a plugin from a marketplace (name, or name@marketplace); returns its id.</summary>
        Public Shared Async Function InstallAsync(name As String, Optional marketplace As String = Nothing) As Task(Of String)
            Dim parts = If(name, "").Trim().Split("@"c)
            Dim n = parts(0)
            Dim mk = If(String.IsNullOrWhiteSpace(marketplace), If(parts.Length > 1, parts(1), Nothing), marketplace)
            Dim market = Marketplaces().Where(Function(m) mk Is Nothing OrElse m.Name = mk).FirstOrDefault(Function(m) m.Plugins.Any(Function(p) p.Name = n))
            If market Is Nothing Then Throw New ArgumentException(If(mk Is Nothing, $"No marketplace lists a plugin {n}.", $"No plugin {n} in the marketplace {mk}."))
            Dim doc = MarketDoc(market.Dir)
            Dim entry = TryCast(doc("plugins"), JsonArray).FirstOrDefault(Function(p) Str(p, "name") = n)
            Dim tmpRoot = Path.Combine(AgenticDir(), "cache", ".new-" & DateTime.UtcNow.Ticks.ToString())
            Dim src As String
            Dim s = entry("source")
            If s Is Nothing OrElse TypeOf s Is JsonValue Then
                Dim rel = If(s Is Nothing, n, s.ToString())
                Dim pluginRoot = Str(TryCast(doc("metadata"), JsonObject), "pluginRoot")
                Dim baseDir = If(rel.StartsWith(".") OrElse rel.StartsWith("/") OrElse pluginRoot.Length = 0, market.Dir, Path.GetFullPath(Path.Combine(market.Dir, pluginRoot)))
                src = Path.GetFullPath(Path.Combine(baseDir, rel))
                If Not Directory.Exists(src) Then Throw New InvalidOperationException($"The plugin's folder {src} is missing from the marketplace.")
            Else
                Dim url = If(Str(s, "repo").Length > 0, "https://github.com/" & Str(s, "repo") & ".git", Str(s, "url"))
                If url.Length = 0 Then Throw New InvalidOperationException("The plugin's source is not one Agentic can install.")
                Await Task.Run(Sub() RunGit({"clone", "--depth", "1", url, tmpRoot})).ConfigureAwait(False)
                src = If(Str(s, "path").Length > 0, Path.Combine(tmpRoot, Str(s, "path")), tmpRoot)
            End If
            Dim version = Regex.Replace(If(Str(Manifest(src), "version").Length > 0, Str(Manifest(src), "version"), If(Str(entry, "version").Length > 0, Str(entry, "version"), "latest")), "[^\w.+-]", "_")
            Dim target = Path.Combine(AgenticDir(), "cache", market.Name, n, version)
            If Directory.Exists(target) Then DeleteTree(target)
            CopyTree(src, target)
            If Directory.Exists(tmpRoot) Then DeleteTree(tmpRoot)
            Dim id = n & "@" & market.Name
            Dim file = Path.Combine(AgenticDir(), "installed_plugins.json")
            Dim reg = If(TryCast(ReadNode(file), JsonObject), New JsonObject())
            If reg("version") Is Nothing Then reg("version") = 2
            Dim plugins = TryCast(reg("plugins"), JsonObject)
            If plugins Is Nothing Then
                plugins = New JsonObject()
                reg("plugins") = plugins
            End If
            Dim nowText = DateTime.UtcNow.ToString("o")
            plugins(id) = New JsonArray(New JsonObject From {{"scope", "user"}, {"installPath", target}, {"version", version}, {"installedAt", nowText}, {"lastUpdated", nowText}})
            WriteNode(file, reg)
            SetEnabled(id, True)
            Return id
        End Function

        Public Shared Sub Uninstall(id As String)
            Dim file = Path.Combine(AgenticDir(), "installed_plugins.json")
            Dim reg = If(TryCast(ReadNode(file), JsonObject), New JsonObject())
            Dim plugins = TryCast(reg("plugins"), JsonObject)
            Dim entries = plugins?(If(id, ""))
            If entries Is Nothing Then Throw New ArgumentException($"{id} was not installed by Agentic (Claude Code's plugins are removed with Claude Code; switch it off here instead).")
            For Each e In If(TypeOf entries Is JsonArray, DirectCast(entries, JsonArray).ToList(), New List(Of JsonNode) From {entries})
                Dim dir = Str(e, "installPath")
                If dir.Length > 0 AndAlso Path.GetFullPath(dir).StartsWith(Path.GetFullPath(AgenticDir()), StringComparison.OrdinalIgnoreCase) AndAlso Directory.Exists(dir) Then DeleteTree(dir)
            Next
            plugins.Remove(id)
            WriteNode(file, reg)
            Dim sfile = Path.Combine(AgenticDir(), "settings.json")
            Dim sdoc = TryCast(ReadNode(sfile), JsonObject)
            Dim ep = TryCast(sdoc?("enabledPlugins"), JsonObject)
            If ep IsNot Nothing AndAlso ep.Remove(id) Then WriteNode(sfile, sdoc)
        End Sub

        ''' <summary>The plugin a name means: its id (name@marketplace) or its name.</summary>
        Public Shared Function Find(nameOrId As String, Optional projectDirs As IEnumerable(Of String) = Nothing) As PluginInfo
            Dim all = Installed(projectDirs)
            Return If(all.FirstOrDefault(Function(p) String.Equals(p.Id, nameOrId, StringComparison.OrdinalIgnoreCase)),
                      all.FirstOrDefault(Function(p) String.Equals(p.Name, nameOrId, StringComparison.OrdinalIgnoreCase)))
        End Function

    End Class

End Namespace
