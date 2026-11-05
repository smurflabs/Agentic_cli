Option Strict On
Option Explicit On
Option Infer On

Imports System.IO
Imports System.Text.Json.Nodes

Namespace Core

    Friend NotInheritable Class SettingsScope
        ''' <summary>user | project | local | flag | session</summary>
        Public Property Name As String
        Public Property File As String
        Public Property Root As New JsonObject()
        Public Property Writable As Boolean
    End Class

    Friend NotInheritable Class HookEntry
        Public Property EventName As String
        Public Property Matcher As String
        Public Property Command As String
        Public Property TimeoutSeconds As Integer = 60
        Public Property Scope As String
    End Class

    ''' <summary>
    ''' Settings in the same layout as Claude Code, merged from (later wins; lists are combined):
    '''   ~\.agentic\settings.json            user
    '''   &lt;project&gt;\.agentic\settings.json     project (shared with the team)
    '''   &lt;project&gt;\.agentic\settings.local.json  local (yours, not committed)
    '''   --settings &lt;file&gt;                    flag
    '''   session                               changes made during this run (not saved)
    ''' </summary>
    Friend NotInheritable Class Settings

        Public ReadOnly Property Scopes As New List(Of SettingsScope)
        Public ReadOnly Property ProjectDir As String
        Private ReadOnly _extraFile As String

        Private Sub New(projectDir As String, extraFile As String)
            Me.ProjectDir = projectDir
            _extraFile = extraFile
        End Sub

        Public Shared Function Load(projectDir As String, Optional extraFile As String = Nothing) As Settings
            Dim s As New Settings(projectDir, extraFile)
            s.Reload()
            Return s
        End Function

        Public Sub Reload()
            Dim session = Scopes.FirstOrDefault(Function(x) x.Name = "session")
            Scopes.Clear()
            Add("user", Paths.UserSettingsFile(), True)
            Add("project", Path.Combine(ProjectDir, ".agentic", "settings.json"), True)
            Add("local", Path.Combine(ProjectDir, ".agentic", "settings.local.json"), True)
            If Not String.IsNullOrWhiteSpace(_extraFile) Then
                If File.Exists(_extraFile) Then
                    Add("flag", _extraFile, False)
                Else
                    ' --settings may also be inline JSON.
                    Scopes.Add(New SettingsScope With {.Name = "flag", .File = Nothing, .Root = Jx.ParseObject(_extraFile)})
                End If
            End If
            Scopes.Add(If(session, New SettingsScope With {.Name = "session", .File = Nothing}))
        End Sub

        Private Sub Add(name As String, file As String, writable As Boolean)
            Scopes.Add(New SettingsScope With {.Name = name, .File = file, .Root = Jx.ParseObject(Paths.ReadText(file)), .Writable = writable})
        End Sub

        Public Function Scope(name As String) As SettingsScope
            Return Scopes.FirstOrDefault(Function(x) x.Name = name)
        End Function

        ' ------------------------------------------------------------------ reading

        Private Shared Function Walk(root As JsonObject, key As String) As JsonNode
            Dim node As JsonNode = root
            For Each part In key.Split("."c)
                Dim obj = TryCast(node, JsonObject)
                If obj Is Nothing Then Return Nothing
                node = obj(part)
                If node Is Nothing Then Return Nothing
            Next
            Return node
        End Function

        ''' <summary>The value from the most specific scope that sets it.</summary>
        Public Function Value(key As String) As JsonNode
            For i = Scopes.Count - 1 To 0 Step -1
                Dim n = Walk(Scopes(i).Root, key)
                If n IsNot Nothing Then Return n
            Next
            Return Nothing
        End Function

        Public Function Str(key As String, Optional fallback As String = Nothing) As String
            Dim s = Jx.NodeStr(Value(key), Nothing)
            Return If(String.IsNullOrWhiteSpace(s), fallback, s)
        End Function

        Public Function Bool(key As String, fallback As Boolean) As Boolean
            Return Jx.NodeBool(Value(key), fallback)
        End Function

        Public Function Int(key As String, fallback As Integer) As Integer
            Return Jx.NodeInt(Value(key), fallback)
        End Function

        ''' <summary>A list setting combined across scopes, with the scope each entry comes from.</summary>
        Public Function Entries(key As String) As List(Of (Scope As String, Value As String))
            Dim list As New List(Of (String, String))
            For Each s In Scopes
                For Each v In Jx.NodeStrings(Walk(s.Root, key))
                    list.Add((s.Name, v))
                Next
            Next
            Return list
        End Function

        Public Function Strings(key As String) As List(Of String)
            Return Entries(key).Select(Function(e) e.Value).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
        End Function

        ''' <summary>Environment variables to set for tools and hooks ("env" in every scope).</summary>
        Public Function EnvVars() As Dictionary(Of String, String)
            Dim d As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
            For Each s In Scopes
                Dim env = TryCast(s.Root("env"), JsonObject)
                If env Is Nothing Then Continue For
                For Each kv In env
                    d(kv.Key) = Jx.NodeStr(kv.Value, "")
                Next
            Next
            Return d
        End Function

        ''' <summary>The hooks of the Claude Code plugins whose hooks run (/plugin), kept for a few seconds (asked for every tool call).</summary>
        Private _pluginHooks As List(Of (Name As String, Root As JsonObject))
        Private _pluginHooksAt As DateTime
        Private Function PluginHookScopes() As List(Of (Name As String, Root As JsonObject))
            If _pluginHooks Is Nothing OrElse (DateTime.UtcNow - _pluginHooksAt).TotalSeconds > 10 Then
                Dim list As New List(Of (String, JsonObject))
                Try
                    For Each p In Plugins.Enabled({ProjectDir}).Where(Function(x) x.HooksOn)
                        Dim h = Plugins.HooksOf(p.Dir)
                        If h.Count > 0 Then list.Add(("plugin " & p.Name, New JsonObject From {{"hooks", h}}))
                    Next
                Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
                End Try
                _pluginHooks = list
                _pluginHooksAt = DateTime.UtcNow
            End If
            Return _pluginHooks
        End Function

        ''' <summary>Hook commands for an event, from every scope (Claude Code's "hooks" layout) and the plugins whose hooks run.</summary>
        Public Function Hooks(eventName As String) As List(Of HookEntry)
            Dim list As New List(Of HookEntry)
            Dim sources = Scopes.Select(Function(s) (s.Name, s.Root)).Concat(PluginHookScopes()).ToList()
            For Each s In sources
                Dim hooksObj = TryCast(s.Root("hooks"), JsonObject)
                Dim groups = TryCast(hooksObj?(eventName), JsonArray)
                If groups Is Nothing Then Continue For
                For Each g In groups.OfType(Of JsonObject)()
                    Dim matcher = Jx.NodeStr(g("matcher"), "")
                    Dim inner = TryCast(g("hooks"), JsonArray)
                    If inner Is Nothing Then
                        ' A bare {"command": ...} entry.
                        Dim cmd = Jx.NodeStr(g("command"), Nothing)
                        If cmd IsNot Nothing Then list.Add(New HookEntry With {.EventName = eventName, .Matcher = matcher, .Command = cmd, .TimeoutSeconds = Jx.NodeInt(g("timeout"), 60), .Scope = s.Name})
                        Continue For
                    End If
                    For Each h In inner.OfType(Of JsonObject)()
                        If Jx.NodeStr(h("type"), "command") <> "command" Then Continue For
                        Dim cmd = Jx.NodeStr(h("command"), Nothing)
                        If String.IsNullOrWhiteSpace(cmd) Then Continue For
                        list.Add(New HookEntry With {.EventName = eventName, .Matcher = matcher, .Command = cmd, .TimeoutSeconds = Math.Max(1, Jx.NodeInt(h("timeout"), 60)), .Scope = s.Name})
                    Next
                Next
            Next
            Return list
        End Function

        ' ------------------------------------------------------------------ writing

        Public Sub SetValue(scopeName As String, key As String, node As JsonNode)
            Dim s = Scope(scopeName)
            If s Is Nothing Then Throw New ArgumentException("Unknown settings scope: " & scopeName)
            Dim parts = key.Split("."c)
            Dim obj = s.Root
            For i = 0 To parts.Length - 2
                Dim nextObj = TryCast(obj(parts(i)), JsonObject)
                If nextObj Is Nothing Then
                    nextObj = New JsonObject()
                    obj(parts(i)) = nextObj
                End If
                obj = nextObj
            Next
            If node Is Nothing Then obj.Remove(parts.Last()) Else obj(parts.Last()) = node
            Save(s)
        End Sub

        Public Function AddToList(scopeName As String, key As String, item As String) As Boolean
            Dim s = Scope(scopeName)
            Dim existing = TryCast(Walk(s.Root, key), JsonArray)
            Dim values = Jx.NodeStrings(existing)
            If values.Any(Function(v) v.Equals(item, StringComparison.OrdinalIgnoreCase)) Then Return False
            values.Add(item)
            SetValue(scopeName, key, New JsonArray(values.Select(Function(v) CType(JsonValue.Create(v), JsonNode)).ToArray()))
            Return True
        End Function

        Public Function RemoveFromList(scopeName As String, key As String, item As String) As Boolean
            Dim s = Scope(scopeName)
            Dim values = Jx.NodeStrings(Walk(s.Root, key))
            Dim removed = values.RemoveAll(Function(v) v.Equals(item, StringComparison.OrdinalIgnoreCase)) > 0
            If removed Then SetValue(scopeName, key, New JsonArray(values.Select(Function(v) CType(JsonValue.Create(v), JsonNode)).ToArray()))
            Return removed
        End Function

        Private Shared Sub Save(s As SettingsScope)
            If Not s.Writable OrElse s.File Is Nothing Then Return
            Paths.WriteText(s.File, s.Root.ToJsonString(Jx.PrettyOptions) & Environment.NewLine)
            ' The local file is personal: keep it out of git like Claude Code does.
            If s.Name = "local" Then EnsureIgnored(s.File)
        End Sub

        Private Shared Sub EnsureIgnored(file As String)
            Try
                Dim dir = Path.GetDirectoryName(file)
                Dim ignore = Path.Combine(dir, ".gitignore")
                Dim text = If(Paths.ReadText(ignore), "")
                If Not text.Split({vbLf, vbCr}, StringSplitOptions.RemoveEmptyEntries).Any(Function(l) l.Trim() = "settings.local.json") Then
                    IO.File.AppendAllText(ignore, If(text.Length > 0 AndAlso Not text.EndsWith(vbLf), Environment.NewLine, "") & "settings.local.json" & Environment.NewLine)
                End If
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
            End Try
        End Sub

    End Class

End Namespace
