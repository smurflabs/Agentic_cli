Option Strict On
Option Explicit On
Option Infer On

Imports System.Text.RegularExpressions
Imports AgenticCli.Core
Imports AgenticCli.Ui

Namespace Agent

    Partial Friend NotInheritable Class Session

        ' ------------------------------------------------------------------ /keys: every key of the CLI, changeable

        ''' <summary>
        ''' /keys picks an action and records its new key; /keys list shows them all; /keys set &lt;action&gt; &lt;keys&gt; (comma
        ''' separated, "none" for no key); /keys reset &lt;action&gt;|all. Saved in the user settings, so they stay after a restart.
        ''' </summary>
        Private Sub KeysCommand(rest As String)
            Dim parts = rest.Split({" "c}, 3, StringSplitOptions.RemoveEmptyEntries)
            Dim verb = If(parts.Length > 0, parts(0).ToLowerInvariant(), "")
            Select Case verb
                Case "", "edit" : If State.Interactive AndAlso Ansi.Interactive Then EditKey() Else ListKeys()
                Case "list", "show" : ListKeys()
                Case "set" : SetKeysCommand(parts)
                Case "reset" : ResetKeysCommand(If(parts.Length > 1, parts(1), ""))
                Case Else : Out.Warning("Usage: /keys [list | set <action> <keys> | reset <action>|all]")
            End Select
        End Sub

        Private Sub ListKeys()
            For Each g In KeyBindings.Actions.GroupBy(Function(a) a.Group)
                Out.Line(Ansi.Bold(g.Key))
                For Each a In g
                    Out.Line("  " & Ansi.Accent(Ansi.Pad(KeyBindings.Describe(a.Id), 30)) & " " & Ansi.Pad(a.Label, 52) & " " &
                             Ansi.Muted(a.Id & If(KeyBindings.IsChanged(a.Id), " · changed", "")))
                Next
            Next
            For Each p In KeyBindings.Problems
                Out.Warning(p)
            Next
            Out.Line(Ansi.Muted($"Change one: /keys (pick it and press the new key) or /keys set <action> <keys>, e.g. /keys set verbose F2. Saved in {Paths.UserSettingsFile()}."))
        End Sub

        ''' <summary>Picks an action from a list and records its new key.</summary>
        Private Sub EditKey()
            Dim options = KeyBindings.Actions.Select(Function(a) New MenuOption($"{a.Group} · {a.Label}",
                KeyBindings.Describe(a.Id) & If(KeyBindings.IsChanged(a.Id), " (changed)", ""), a.Id)).ToList()
            Dim i = Menu.Select("Which key do you want to change?", options, filterable:=True)
            If i < 0 Then Return
            Dim action = KeyBindings.Actions(i)
            Out.Line($"Press the new key for {Ansi.Bold(action.Label)} - two keys one after the other also work ({Ansi.Muted("now: " & KeyBindings.Describe(action.Id))})")
            Dim combo = CaptureKey()
            Dim problem = KeyProblem(action, combo)
            If problem IsNot Nothing Then Out.Warning(problem) : Return
            Dim current = KeyBindings.KeysOf(action.Id)
            Dim choices As New List(Of MenuOption) From {
                New MenuOption($"Use {combo}" & If(current.Length > 0, $" instead of {KeyBindings.Describe(action.Id)}", ""), value:="use"),
                New MenuOption($"Add {combo} as another key", value:="add"),
                New MenuOption("Remove its keys (no key)", value:="none"),
                New MenuOption($"Back to the default ({If(action.Defaults.Length > 0, String.Join(", ", action.Defaults), "none")})", value:="reset"),
                New MenuOption("Cancel", value:="cancel")}
            Dim c = Menu.Select($"{combo} · {action.Label}", choices)
            If c < 0 Then Return
            Select Case choices(c).Value
                Case "use" : SaveKeys(action, {combo})
                Case "add" : SaveKeys(action, current.Where(Function(k) Not k.Equals(combo, StringComparison.OrdinalIgnoreCase)).Concat({combo}))
                Case "none" : SaveKeys(action, Array.Empty(Of String)())
                Case "reset" : SaveKeys(action, action.Defaults)
            End Select
        End Sub

        ''' <summary>Reads one key, or two pressed right after each other ("Esc Esc").</summary>
        Private Function CaptureKey() As String
            Menu.InputLock.Wait()
            Try
                Dim first = KeyBindings.Chord(Menu.Term.ReadKey())
                If first.Length = 0 Then Return ""
                Dim until = DateTime.UtcNow.AddMilliseconds(KeyBindings.SequenceMs)
                While DateTime.UtcNow < until
                    If Menu.Term.KeyAvailable Then
                        Dim second = KeyBindings.Chord(Menu.Term.ReadKey())
                        Return If(second.Length > 0, first & " " & second, first)
                    End If
                    Threading.Thread.Sleep(20)
                End While
                Return first
            Finally
                Menu.InputLock.Release()
            End Try
        End Function

        ''' <summary>Why this key cannot be used for the action (Nothing when it can): a plain character would be typed.</summary>
        Private Shared Function KeyProblem(action As KeyBindings.KeyAction, combo As String) As String
            If String.IsNullOrEmpty(combo) Then Return "That key cannot be used: choose another one."
            Dim first = combo.Split(" "c)(0)
            If action.Group <> "Menus" AndAlso Regex.IsMatch(first, "^(Shift\+)?(.|Space)$") Then
                Return $"{combo} types a character in the message box: choose a key with Ctrl or Alt, or a key like Enter, Tab, Esc or F2."
            End If
            Return Nothing
        End Function

        ''' <summary>Saves the keys, after asking to take them away from other actions that use them in the same place.</summary>
        Private Sub SaveKeys(action As KeyBindings.KeyAction, keys As IEnumerable(Of String))
            Dim list = keys.ToList()
            For Each combo In list
                Dim others = KeyBindings.Conflicts(action.Id, combo)
                If others.Count = 0 Then Continue For
                Dim names = String.Join(" and ", others.Select(Function(o) $"'{o.Label}'"))
                If State.Interactive AndAlso Ansi.Interactive Then
                    If Not Menu.Confirm($"{combo} is used for {names}. Use it for '{action.Label}' and remove it there?") Then Return
                    For Each o In others
                        KeyBindings.Save(State.Settings, o.Id, KeyBindings.KeysOf(o.Id).Where(Function(k) Not k.Equals(combo, StringComparison.OrdinalIgnoreCase)))
                    Next
                Else
                    Out.Warning($"{combo} is also used for {names}: the first one checked wins. Change it there with /keys set.")
                End If
            Next
            KeyBindings.Save(State.Settings, action.Id, list)
            Out.Success($"{action.Label}: {KeyBindings.Describe(action.Id)}")
        End Sub

        Private Sub SetKeysCommand(parts As String())
            If parts.Length < 3 Then Out.Warning("Usage: /keys set <action> <keys>   e.g. /keys set verbose F2   or   /keys set submit Ctrl+Enter, Enter   (none = no key)") : Return
            Dim action = KeyBindings.Find(parts(1))
            If action Is Nothing Then Out.Warning($"Unknown action '{parts(1)}': /keys list shows them.") : Return
            Dim keys As New List(Of String)
            If Not parts(2).Trim().Equals("none", StringComparison.OrdinalIgnoreCase) Then
                For Each typed In parts(2).Split(","c).Select(Function(t) t.Trim()).Where(Function(t) t.Length > 0)
                    Dim combo = KeyBindings.Normalize(typed)
                    Dim problem = KeyProblem(action, combo)
                    If combo.Length = 0 Then problem = $"'{typed}' is not a key: write it like Ctrl+O, Alt+B, Shift+Tab, Esc, F2 or Esc Esc."
                    If problem IsNot Nothing Then Out.Warning(problem) : Return
                    keys.Add(combo)
                Next
            End If
            SaveKeys(action, keys)
        End Sub

        Private Sub ResetKeysCommand(what As String)
            If what.Equals("all", StringComparison.OrdinalIgnoreCase) Then
                KeyBindings.ResetAll(State.Settings)
                Out.Success("Every key is back to its default.")
                Return
            End If
            Dim action = KeyBindings.Find(what)
            If action Is Nothing Then Out.Warning("Usage: /keys reset <action>|all   (/keys list shows the actions)") : Return
            KeyBindings.Save(State.Settings, action.Id, action.Defaults)
            Out.Success($"{action.Label}: {KeyBindings.Describe(action.Id)}")
        End Sub

    End Class

End Namespace
