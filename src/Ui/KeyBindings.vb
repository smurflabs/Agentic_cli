Option Strict On
Option Explicit On
Option Infer On

Imports System.Text.Json.Nodes
Imports System.Text.RegularExpressions

Namespace Ui

    ''' <summary>
    ''' Every key of the CLI, changeable with /keys. A key is written like "Ctrl+O", "Alt+B", "Shift+Tab", "Esc", "F2",
    ''' "Ctrl+Enter", or two keys pressed one after the other: "Esc Esc". Only the keys the user changed are kept, in the user
    ''' settings file (~\.agentic\settings.json, "keybindings": {"verbose": ["F2"]}); a project's settings can add their own.
    ''' </summary>
    Friend NotInheritable Class KeyBindings

        Friend NotInheritable Class KeyAction
            Public Property Id As String
            Public Property Group As String
            Public Property Label As String
            Public Property Defaults As String()
        End Class

        Public Const SettingsKey As String = "keybindings"
        ''' <summary>Two keys count as a pair ("Esc Esc") when the second comes this soon after the first.</summary>
        Public Const SequenceMs As Integer = 800
        Public Const MaxKeys As Integer = 4

        Private Shared Function A(id As String, group As String, label As String, ParamArray keys As String()) As KeyAction
            Return New KeyAction With {.Id = id, .Group = group, .Label = label, .Defaults = keys}
        End Function

        Public Shared ReadOnly Actions As KeyAction() = {
            A("submit", "Message box", "Send", "Enter"),
            A("newline", "Message box", "New line", "Ctrl+Enter", "Alt+Enter", "Shift+Enter", "Ctrl+J"),
            A("accept-suggestion", "Message box", "Take the highlighted suggestion", "Tab"),
            A("cycle-mode", "Message box", "Cycle the permission mode", "Shift+Tab"),
            A("up", "Message box", "Up: suggestion, line or older message", "Up"),
            A("down", "Message box", "Down: suggestion, line or newer message", "Down"),
            A("left", "Message box", "Cursor left", "Left", "Ctrl+B"),
            A("right", "Message box", "Cursor right (at the end: take the suggestion)", "Right", "Ctrl+F"),
            A("word-left", "Message box", "Word left", "Ctrl+Left", "Alt+Left", "Alt+B"),
            A("word-right", "Message box", "Word right", "Ctrl+Right", "Alt+Right", "Alt+F"),
            A("line-start", "Message box", "Start of the line", "Home", "Ctrl+A"),
            A("line-end", "Message box", "End of the line", "End", "Ctrl+E"),
            A("start", "Message box", "Start of the message", "Ctrl+Home"),
            A("end", "Message box", "End of the message", "Ctrl+End"),
            A("backspace", "Message box", "Delete the character before the cursor", "Backspace", "Ctrl+H"),
            A("delete", "Message box", "Delete the character at the cursor", "Delete"),
            A("delete-word", "Message box", "Delete the word before the cursor", "Ctrl+Backspace", "Alt+Backspace", "Ctrl+W"),
            A("delete-to-start", "Message box", "Delete to the start of the line", "Ctrl+U"),
            A("delete-to-end", "Message box", "Delete to the end of the line", "Ctrl+K"),
            A("undo", "Message box", "Undo", "Ctrl+Z", "Ctrl+-"),
            A("clear-screen", "Message box", "Clear the screen", "Ctrl+L"),
            A("history-search", "Message box", "Search older messages", "Ctrl+R"),
            A("dismiss", "Message box", "Hide suggestions (Vim keys: normal mode)", "Esc"),
            A("clear", "Message box", "Clear the message box", "Esc Esc"),
            A("rewind", "Message box", "Go back to an earlier message (in an empty box)", "Esc Esc"),
            A("interrupt", "Message box", "Clear the box (twice in an empty box: quit)", "Ctrl+C"),
            A("exit", "Message box", "Quit (in an empty box; else delete forward)", "Ctrl+D"),
            A("todos", "Message box", "Show the todo list", "Ctrl+T"),
            A("external-editor", "Message box", "Edit the message in your editor ($VISUAL / $EDITOR)", "Ctrl+G"),
            A("paste-image", "Message box", "Paste an image from the clipboard (attached when sent)", "Alt+V"),
            A("verbose", "Anytime", "Verbose: full tool output and thinking", "Ctrl+O"),
            A("stop", "While answering", "Stop the answer", "Esc", "Ctrl+C"),
            A("queue", "While answering", "Queue what you typed as the next message", "Enter"),
            A("search-next", "History search", "Older match", "Ctrl+R"),
            A("search-accept", "History search", "Use the match", "Enter", "Tab", "Left", "Right"),
            A("search-cancel", "History search", "Cancel the search", "Esc", "Ctrl+C", "Ctrl+G"),
            A("menu-up", "Menus", "Up (K in menus without a filter)", "Up", "K"),
            A("menu-down", "Menus", "Down (J in menus without a filter)", "Down", "J", "Tab"),
            A("menu-page-up", "Menus", "Page up", "PageUp"),
            A("menu-page-down", "Menus", "Page down", "PageDown"),
            A("menu-first", "Menus", "First", "Home"),
            A("menu-last", "Menus", "Last", "End"),
            A("menu-pick", "Menus", "Choose", "Enter"),
            A("menu-cancel", "Menus", "Clear the filter, then cancel", "Esc", "Ctrl+C"),
            A("menu-clear-filter", "Menus", "Clear the filter", "Ctrl+U")}

        Private Shared ReadOnly Sync As New Object()
        Private Shared _map As Dictionary(Of String, String()) = Defaults()
        ''' <summary>Changed keys that could not be read (shown by /keys).</summary>
        Public Shared ReadOnly Property Problems As New List(Of String)

        Private Shared Function Defaults() As Dictionary(Of String, String())
            Return Actions.ToDictionary(Function(x) x.Id, Function(x) x.Defaults, StringComparer.OrdinalIgnoreCase)
        End Function

        Public Shared Function Find(id As String) As KeyAction
            Return Actions.FirstOrDefault(Function(x) x.Id.Equals(If(id, ""), StringComparison.OrdinalIgnoreCase))
        End Function

        ''' <summary>The keys of an action: the user's, else the default ones.</summary>
        Public Shared Function KeysOf(id As String) As String()
            SyncLock Sync
                Dim keys As String() = Nothing
                Return If(_map.TryGetValue(id, keys), keys, Array.Empty(Of String)())
            End SyncLock
        End Function

        ''' <summary>The first key of an action for hints ("Ctrl+O"); "" when it has none.</summary>
        Public Shared Function Label(id As String) As String
            Dim keys = KeysOf(id)
            Return If(keys.Length > 0, keys(0), "")
        End Function

        ''' <summary>The first key of an action, short and lower case for footers ("↑", "enter", "ctrl+o"); "-" when it has none.</summary>
        Public Shared Function ShortLabel(id As String) As String
            Dim k = Label(id)
            Select Case k
                Case "" : Return "-"
                Case "Up" : Return "↑"
                Case "Down" : Return "↓"
                Case "Left" : Return "←"
                Case "Right" : Return "→"
                Case "PageUp" : Return "PgUp"
                Case "PageDown" : Return "PgDn"
            End Select
            Return k.ToLowerInvariant()
        End Function

        ''' <summary>All keys of an action for help texts ("Ctrl+O" or "Esc, Ctrl+C"); "none" when it has none.</summary>
        Public Shared Function Describe(id As String) As String
            Dim keys = KeysOf(id)
            Return If(keys.Length > 0, String.Join(", ", keys), "none")
        End Function

        Public Shared Function IsChanged(id As String) As Boolean
            Dim a = Find(id)
            Return a IsNot Nothing AndAlso Not KeysOf(id).SequenceEqual(a.Defaults, StringComparer.OrdinalIgnoreCase)
        End Function

        ' ------------------------------------------------------------------ loading and saving

        ''' <summary>Reads the changed keys from every settings scope (user, then project, local …; later wins per action).</summary>
        Public Shared Sub Load(settings As Core.Settings)
            Dim map = Defaults()
            Problems.Clear()
            If settings IsNot Nothing Then
                For Each scope In settings.Scopes
                    Dim obj = TryCast(scope.Root(SettingsKey), JsonObject)
                    If obj Is Nothing Then Continue For
                    For Each kv In obj
                        Dim a = Find(kv.Key)
                        If a Is Nothing Then Problems.Add($"Unknown key action '{kv.Key}' in {scope.Name} settings.") : Continue For
                        Dim texts As New List(Of String)
                        If TypeOf kv.Value Is JsonArray Then
                            texts.AddRange(DirectCast(kv.Value, JsonArray).Select(Function(n) If(n Is Nothing, "", n.ToString())))
                        ElseIf kv.Value IsNot Nothing Then
                            texts.Add(kv.Value.ToString())
                        End If
                        Dim keys As New List(Of String)
                        For Each t In texts
                            Dim n = Normalize(t)
                            If n.Length = 0 Then
                                Problems.Add($"{a.Id}: '{t}' is not a key ({scope.Name} settings).")
                            ElseIf Not keys.Contains(n) Then
                                keys.Add(n)
                            End If
                        Next
                        map(a.Id) = keys.Take(MaxKeys).ToArray()
                    Next
                Next
            End If
            SyncLock Sync
                _map = map
            End SyncLock
        End Sub

        ''' <summary>Saves an action's keys to the user settings (the default keys remove the entry) and applies them.</summary>
        Public Shared Sub Save(settings As Core.Settings, id As String, keys As IEnumerable(Of String))
            Dim a = Find(id)
            If a Is Nothing Then Throw New ArgumentException("Unknown action: " & id)
            Dim list = keys.Where(Function(k) Not String.IsNullOrWhiteSpace(k)).Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxKeys).ToList()
            Dim node As JsonNode = Nothing
            If Not list.SequenceEqual(a.Defaults, StringComparer.OrdinalIgnoreCase) Then node = New JsonArray(list.Select(Function(k) CType(JsonValue.Create(k), JsonNode)).ToArray())
            settings.SetValue("user", SettingsKey & "." & a.Id, node)
            Load(settings)
        End Sub

        ''' <summary>Every key back to its default (in the user settings).</summary>
        Public Shared Sub ResetAll(settings As Core.Settings)
            settings.SetValue("user", SettingsKey, Nothing)
            Load(settings)
        End Sub

        ' ------------------------------------------------------------------ what was pressed

        ''' <summary>The key pressed, written the usual way ("Ctrl+O", "Shift+Tab", "Esc"); "" for a plain character key.</summary>
        Public Shared Function Chord(k As ConsoleKeyInfo) As String
            Dim ctrl = (k.Modifiers And ConsoleModifiers.Control) <> 0
            Dim alt = (k.Modifiers And ConsoleModifiers.Alt) <> 0
            Dim shift = (k.Modifiers And ConsoleModifiers.Shift) <> 0
            ' Terminals send Ctrl+J as a line feed, which .NET reports as Enter.
            If k.Key = ConsoleKey.Enter AndAlso k.KeyChar = ChrW(10) AndAlso Not ctrl AndAlso Not alt AndAlso Not shift Then Return "Ctrl+J"
            Dim name = KeyName(k.Key)
            If name.Length = 0 Then Return ""
            Return If(ctrl, "Ctrl+", "") & If(alt, "Alt+", "") & If(shift, "Shift+", "") & name
        End Function

        Private Shared Function KeyName(key As ConsoleKey) As String
            Select Case key
                Case ConsoleKey.Enter : Return "Enter"
                Case ConsoleKey.Escape : Return "Esc"
                Case ConsoleKey.Spacebar : Return "Space"
                Case ConsoleKey.UpArrow : Return "Up"
                Case ConsoleKey.DownArrow : Return "Down"
                Case ConsoleKey.LeftArrow : Return "Left"
                Case ConsoleKey.RightArrow : Return "Right"
                Case ConsoleKey.OemMinus, ConsoleKey.Subtract : Return "-"
                Case ConsoleKey.OemPlus : Return "="
                Case ConsoleKey.OemComma : Return ","
                Case ConsoleKey.OemPeriod : Return "."
                Case ConsoleKey.Oem1 : Return ";"
                Case ConsoleKey.Oem2 : Return "/"
                Case ConsoleKey.Oem3 : Return "`"
                Case ConsoleKey.Oem4 : Return "["
                Case ConsoleKey.Oem5 : Return "\"
                Case ConsoleKey.Oem6 : Return "]"
                Case ConsoleKey.Oem7 : Return "'"
            End Select
            If CInt(key) = 0 Then Return ""   ' a character without a key of its own (pasted, typed with an input method)
            If key >= ConsoleKey.D0 AndAlso key <= ConsoleKey.D9 Then Return CStr(CInt(key) - CInt(ConsoleKey.D0))
            If key >= ConsoleKey.NumPad0 AndAlso key <= ConsoleKey.NumPad9 Then Return "Num" & CStr(CInt(key) - CInt(ConsoleKey.NumPad0))
            Return key.ToString()   ' A-Z, Tab, Backspace, Delete, Insert, Home, End, PageUp, PageDown, F1-F24 …
        End Function

        Private Shared ReadOnly NamedKeys As String() = {"Enter", "Esc", "Space", "Up", "Down", "Left", "Right", "Tab", "Backspace", "Delete", "Insert",
            "Home", "End", "PageUp", "PageDown", "Pause", "PrintScreen"}
        Private Shared ReadOnly Aliases As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase) From {
            {"escape", "Esc"}, {"return", "Enter"}, {"spacebar", "Space"}, {"uparrow", "Up"}, {"downarrow", "Down"}, {"leftarrow", "Left"},
            {"rightarrow", "Right"}, {"del", "Delete"}, {"ins", "Insert"}, {"bksp", "Backspace"}, {"back", "Backspace"}, {"pgup", "PageUp"},
            {"pgdn", "PageDown"}, {"pagedown", "PageDown"}, {"pageup", "PageUp"}, {"↑", "Up"}, {"↓", "Down"}, {"←", "Left"}, {"→", "Right"}}

        ''' <summary>A key written by the user, in the usual form ("ctrl+o" → "Ctrl+O", "esc esc" → "Esc Esc"); "" when it is not one.</summary>
        Public Shared Function Normalize(text As String) As String
            If String.IsNullOrWhiteSpace(text) Then Return ""
            Dim chords = text.Trim().Split({" "c, ChrW(9)}, StringSplitOptions.RemoveEmptyEntries)
            If chords.Length > 2 Then Return ""
            Dim out As New List(Of String)
            For Each c In chords
                Dim m = Regex.Match(c, "^((?:(?:ctrl|control|alt|shift)\+)*)(.+)$", RegexOptions.IgnoreCase)
                If Not m.Success Then Return ""
                Dim mods = m.Groups(1).Value.ToLowerInvariant()
                Dim key = NormalizeKey(m.Groups(2).Value)
                If key.Length = 0 Then Return ""
                out.Add(If(mods.Contains("ctrl") OrElse mods.Contains("control"), "Ctrl+", "") & If(mods.Contains("alt"), "Alt+", "") &
                        If(mods.Contains("shift"), "Shift+", "") & key)
            Next
            Return String.Join(" ", out)
        End Function

        Private Shared Function NormalizeKey(k As String) As String
            Dim a As String = Nothing
            If Aliases.TryGetValue(k, a) Then Return a
            Dim named = NamedKeys.FirstOrDefault(Function(n) n.Equals(k, StringComparison.OrdinalIgnoreCase))
            If named IsNot Nothing Then Return named
            If k.Length = 1 Then
                Dim ch = Char.ToUpperInvariant(k(0))
                If (ch >= "A"c AndAlso ch <= "Z"c) OrElse Char.IsDigit(ch) OrElse ";=,-./`[\]'".Contains(ch) Then Return ch.ToString()
                Return ""
            End If
            Dim f = Regex.Match(k, "^f([1-9]|1[0-9]|2[0-4])$", RegexOptions.IgnoreCase)
            If f.Success Then Return "F" & f.Groups(1).Value
            Dim num = Regex.Match(k, "^num(?:pad)?([0-9])$", RegexOptions.IgnoreCase)
            If num.Success Then Return "Num" & num.Groups(1).Value
            Return ""
        End Function

        ''' <summary>
        ''' Whether this press is one of the action's keys. A pair ("Esc Esc") also needs the previous key (prev, pressed at
        ''' prevAt) to be its first half.
        ''' </summary>
        Public Shared Function Matches(id As String, chord As String, Optional prev As String = Nothing, Optional prevAt As DateTime = Nothing) As Boolean
            If String.IsNullOrEmpty(chord) Then Return False
            For Each combo In KeysOf(id)
                Dim parts = combo.Split(" "c)
                If parts.Length = 1 Then
                    If parts(0) = chord Then Return True
                ElseIf parts.Length = 2 AndAlso parts(1) = chord AndAlso parts(0) = prev AndAlso (DateTime.UtcNow - prevAt).TotalMilliseconds < SequenceMs Then
                    Return True
                End If
            Next
            Return False
        End Function

        ''' <summary>The first halves of an action's pairs ("Esc" for "Esc Esc"): pressing one shows what to press next.</summary>
        Public Shared Function PairStarts(id As String) As List(Of (First As String, Second As String))
            Return KeysOf(id).Select(Function(c) c.Split(" "c)).Where(Function(p) p.Length = 2).Select(Function(p) (p(0), p(1))).ToList()
        End Function

        ''' <summary>Actions that use this key where it would also apply (same place, or "Anytime").</summary>
        Public Shared Function Conflicts(id As String, combo As String) As List(Of KeyAction)
            Dim a = Find(id)
            If a Is Nothing Then Return New List(Of KeyAction)
            ' "Anytime" keys work in the message box and while an answer runs (not in menus or the history search).
            Dim box = Function(g As String) g = "Message box" OrElse g = "While answering"
            Dim sameScope = Function(o As KeyAction) o.Group = a.Group OrElse (o.Group = "Anytime" AndAlso box(a.Group)) OrElse (a.Group = "Anytime" AndAlso box(o.Group))
            ' Some defaults share keys on purpose (Esc Esc clears a full box, or goes back from an empty one).
            Dim allowed = {({"clear", "rewind"}), ({"interrupt", "stop"})}
            Return Actions.Where(Function(o) Not o.Id.Equals(a.Id, StringComparison.OrdinalIgnoreCase) AndAlso sameScope(o) AndAlso
                                     KeysOf(o.Id).Contains(combo, StringComparer.OrdinalIgnoreCase) AndAlso
                                     Not allowed.Any(Function(p) p.Contains(a.Id) AndAlso p.Contains(o.Id))).ToList()
        End Function

    End Class

End Namespace
