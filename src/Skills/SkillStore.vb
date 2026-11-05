Option Strict On
Option Explicit On
Option Infer On

Imports System.IO
Imports AgenticCli.Core

Namespace Skills

    Friend NotInheritable Class SkillFileRef
        Public Property Path As String
        Public Property SizeBytes As Long
    End Class

    Friend NotInheritable Class SkillInfo
        ''' <summary>skill (a folder with SKILL.md) or command (a .md file run as /name).</summary>
        Public Property Kind As String = "skill"
        Public Property Name As String
        Public Property Description As String
        Public Property ArgumentHint As String
        Public Property AllowedTools As New List(Of String)
        Public Property Model As String
        ''' <summary>The model may not load it on its own (only you, with /name).</summary>
        Public Property DisableModelInvocation As Boolean
        Public Property Source As String
        Public Property Location As String
        Public Property Body As String
        Public Property Files As New List(Of SkillFileRef)
    End Class

    ''' <summary>
    ''' Claude Code skills and slash commands from every place they are kept (the first one wins per name):
    '''   project   .agentic\skills, .agentic\commands, .claude\skills, .claude\commands
    '''   user      ~\.agentic\skills, ~\.agentic\commands, ~\.claude\skills, ~\.claude\commands
    '''   desktop   the Agentic desktop app's skills and commands (%APPDATA%\AgenticClient)
    '''   plugins   ~\.claude\plugins\cache\&lt;marketplace&gt;\&lt;plugin&gt;\&lt;version&gt;\skills|commands (named plugin:name)
    ''' </summary>
    Friend NotInheritable Class SkillStore

        Private ReadOnly _projectDir As String
        Private ReadOnly _includeClaude As Boolean
        Private _items As New List(Of SkillInfo)
        Private _loadedAt As DateTime

        Public Sub New(projectDir As String, includeClaude As Boolean)
            _projectDir = projectDir
            _includeClaude = includeClaude
        End Sub

        ''' <summary>--disable-slash-commands: no skills or custom commands at all.</summary>
        Public Property Disabled As Boolean

        Public Function All() As List(Of SkillInfo)
            If Disabled Then Return New List(Of SkillInfo)
            If (DateTime.UtcNow - _loadedAt).TotalSeconds > 20 Then Refresh()
            Return _items.ToList()
        End Function

        Public Function Skills() As List(Of SkillInfo)
            Return All().Where(Function(s) s.Kind = "skill").ToList()
        End Function

        Public Function Commands() As List(Of SkillInfo)
            Return All().Where(Function(s) s.Kind = "command").ToList()
        End Function

        Public Sub Refresh()
            Dim items As New List(Of SkillInfo)
            Dim home = Paths.AgenticHome()
            Dim claude = Paths.ClaudeHome()
            LoadSkills(items, Path.Combine(_projectDir, ".agentic", "skills"), "project", Nothing)
            LoadCommands(items, Path.Combine(_projectDir, ".agentic", "commands"), "project", Nothing)
            If _includeClaude Then
                LoadSkills(items, Path.Combine(_projectDir, ".claude", "skills"), "project (.claude)", Nothing)
                LoadCommands(items, Path.Combine(_projectDir, ".claude", "commands"), "project (.claude)", Nothing)
            End If
            LoadSkills(items, Path.Combine(home, "skills"), "user", Nothing)
            LoadCommands(items, Path.Combine(home, "commands"), "user", Nothing)
            If _includeClaude Then
                LoadSkills(items, Path.Combine(claude, "skills"), "Claude Code", Nothing)
                LoadCommands(items, Path.Combine(claude, "commands"), "Claude Code", Nothing)
            End If
            Dim desktop = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AgenticClient")
            LoadSkills(items, Path.Combine(desktop, "skills"), "Agentic Desktop", Nothing)
            LoadCommands(items, Path.Combine(desktop, "commands"), "Agentic Desktop", Nothing)
            ' Claude Code plugins that are on (/plugin): Agentic's own always, Claude Code's with its folders.
            For Each p In Core.Plugins.Enabled({_projectDir}).Where(Function(x) _includeClaude OrElse x.Source = "Agentic")
                For Each d In Core.Plugins.Folders(p.Dir, "skills")
                    LoadSkills(items, d, "plugin " & p.Name, p.Name)
                Next
                For Each d In Core.Plugins.Folders(p.Dir, "commands")
                    LoadCommands(items, d, "plugin " & p.Name, p.Name)
                Next
            Next
            _items = items.GroupBy(Function(s) s.Kind & ":" & s.Name.ToLowerInvariant()).Select(Function(g) g.First()).
                OrderBy(Function(s) s.Kind).ThenBy(Function(s) s.Name, StringComparer.OrdinalIgnoreCase).ToList()
            _loadedAt = DateTime.UtcNow
        End Sub

        Private Shared Sub LoadSkills(items As List(Of SkillInfo), root As String, source As String, plugin As String)
            If Not Directory.Exists(root) Then Return
            Try
                For Each folder As String In Directory.EnumerateDirectories(root)
                    Dim md = Path.Combine(folder, "SKILL.md")
                    If Not File.Exists(md) Then md = Path.Combine(folder, "skill.md")
                    If Not File.Exists(md) Then Continue For
                    Dim parsed = YamlFrontMatter.Split(Paths.ReadText(md))
                    Dim name = Str(parsed.Front, "name", Path.GetFileName(folder))
                    If plugin IsNot Nothing Then name = plugin & ":" & name
                    items.Add(New SkillInfo With {
                        .Kind = "skill", .Name = name, .Description = Str(parsed.Front, "description", ""), .Source = source, .Location = folder,
                        .Body = parsed.Body, .AllowedTools = StrList(parsed.Front, "allowed-tools"), .Model = Str(parsed.Front, "model", Nothing),
                        .DisableModelInvocation = Str(parsed.Front, "disable-model-invocation", "false").Equals("true", StringComparison.OrdinalIgnoreCase),
                        .Files = ListFiles(folder)})
                Next
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
            End Try
        End Sub

        Private Shared Sub LoadCommands(items As List(Of SkillInfo), root As String, source As String, plugin As String)
            If Not Directory.Exists(root) Then Return
            Try
                For Each md In Directory.EnumerateFiles(root, "*.md", SearchOption.AllDirectories).Take(500)
                    Dim rel = Path.GetRelativePath(root, md)
                    Dim baseName = Path.ChangeExtension(rel, Nothing).Replace("\"c, ":"c).Replace("/"c, ":"c)
                    Dim parsed = YamlFrontMatter.Split(Paths.ReadText(md))
                    Dim description = Str(parsed.Front, "description", Nothing)
                    If description Is Nothing Then description = parsed.Body.Split(ChrW(10)).Select(Function(l) l.Trim().TrimStart("#"c).Trim()).FirstOrDefault(Function(l) l.Length > 0)
                    items.Add(New SkillInfo With {
                        .Kind = "command", .Name = If(plugin Is Nothing, baseName, plugin & ":" & baseName), .Description = If(description, ""),
                        .ArgumentHint = Str(parsed.Front, "argument-hint", Nothing), .Source = source, .Location = md, .Body = parsed.Body,
                        .AllowedTools = StrList(parsed.Front, "allowed-tools"), .Model = Str(parsed.Front, "model", Nothing)})
                Next
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
            End Try
        End Sub

        Private Shared Function ListFiles(folder As String) As List(Of SkillFileRef)
            Dim files As New List(Of SkillFileRef)
            Try
                Dim opts As New EnumerationOptions With {.RecurseSubdirectories = True, .IgnoreInaccessible = True, .MaxRecursionDepth = 5}
                For Each f In New DirectoryInfo(folder).EnumerateFiles("*", opts)
                    Dim rel = Path.GetRelativePath(folder, f.FullName).Replace("\"c, "/"c)
                    If rel.Equals("SKILL.md", StringComparison.OrdinalIgnoreCase) OrElse rel.Contains("node_modules/") OrElse rel.StartsWith(".git/") OrElse rel.Contains("__pycache__/") Then Continue For
                    files.Add(New SkillFileRef With {.Path = rel, .SizeBytes = f.Length})
                    If files.Count >= 300 Then Exit For
                Next
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
            End Try
            Return files.OrderBy(Function(f) f.Path, StringComparer.OrdinalIgnoreCase).ToList()
        End Function

        Private Shared Function Str(map As Dictionary(Of String, Object), key As String, fallback As String) As String
            Dim o As Object = Nothing
            If map.TryGetValue(key, o) AndAlso o IsNot Nothing Then
                Dim v = If(TypeOf o Is List(Of String), String.Join(", ", DirectCast(o, List(Of String))), o.ToString())
                If v.Length > 0 Then Return v
            End If
            Return fallback
        End Function

        Private Shared Function StrList(map As Dictionary(Of String, Object), key As String) As List(Of String)
            Dim o As Object = Nothing
            If Not map.TryGetValue(key, o) OrElse o Is Nothing Then Return New List(Of String)
            If TypeOf o Is List(Of String) Then Return DirectCast(o, List(Of String))
            Return o.ToString().Split(","c).Select(Function(t) t.Trim()).Where(Function(t) t.Length > 0).ToList()
        End Function

        ''' <summary>By invocation name ("standup", "/standup", "plugin:skill"); skills before commands.</summary>
        Public Function FindByName(name As String) As SkillInfo
            If String.IsNullOrWhiteSpace(name) Then Return Nothing
            Dim n = name.Trim().TrimStart("/"c)
            Return All().Where(Function(s) s.Name.Equals(n, StringComparison.OrdinalIgnoreCase)).OrderBy(Function(s) If(s.Kind = "skill", 0, 1)).FirstOrDefault()
        End Function

        ''' <summary>A file inside the skill's folder (relative path; nothing outside the folder).</summary>
        Public Shared Function ReadSkillFile(s As SkillInfo, relative As String) As String
            If s Is Nothing OrElse s.Kind <> "skill" OrElse String.IsNullOrWhiteSpace(relative) Then Return Nothing
            Dim full = Path.GetFullPath(Path.Combine(s.Location, relative.Replace("/"c, Path.DirectorySeparatorChar)))
            If Not Paths.IsInside(full, s.Location) OrElse Not File.Exists(full) Then Return Nothing
            Return Paths.ReadText(full)
        End Function

        ''' <summary>A command's prompt with $ARGUMENTS (and $1, $2 …) filled in.</summary>
        Public Shared Function Expand(s As SkillInfo, args As String) As String
            Dim text = If(s.Body, "")
            args = If(args, "").Trim()
            Dim parts = args.Split(" "c, StringSplitOptions.RemoveEmptyEntries)
            For i = 9 To 1 Step -1
                text = text.Replace("$" & i, If(i <= parts.Length, parts(i - 1), ""))
            Next
            If text.Contains("$ARGUMENTS") Then Return text.Replace("$ARGUMENTS", args)
            Return If(args.Length > 0, text & vbLf & vbLf & "Arguments: " & args, text)
        End Function

        ''' <summary>Creates a command or skill file from a template; returns its path.</summary>
        Public Function Create(kind As String, name As String, description As String, body As String, scope As String) As String
            Dim slugName = New String(name.ToLowerInvariant().Select(Function(c) If(Char.IsLetterOrDigit(c) OrElse c = "-"c OrElse c = "_"c, c, "-"c)).ToArray()).Trim("-"c)
            Dim baseDir = If(scope = "project", Path.Combine(_projectDir, ".agentic"), Paths.AgenticHome())
            Dim front = "---" & vbLf & If(kind = "skill", "name: " & slugName & vbLf, "") & "description: " & description & vbLf & "---" & vbLf & vbLf
            Dim file As String
            If kind = "skill" Then
                file = Path.Combine(baseDir, "skills", slugName, "SKILL.md")
            Else
                file = Path.Combine(baseDir, "commands", slugName & ".md")
            End If
            Paths.WriteText(file, front & If(body, "").Trim() & vbLf)
            _loadedAt = DateTime.MinValue
            Return file
        End Function

    End Class

End Namespace
