Option Strict On
Option Explicit On
Option Infer On

Imports System.IO
Imports AgenticCli.Core
Imports AgenticCli.Ui

''' <summary>
''' Self-tests of /keys: keys are read and written the usual way, every action follows its changed keys (in the message box,
''' history search, menus), pairs like Esc Esc keep working, and the changes are saved in the user settings and come back.
''' </summary>
Partial Friend NotInheritable Class SelfTest

    Private Shared Sub KeyBindingTests(tmp As String)
        ' Reading and writing keys.
        Check("keys: written the usual way", KeyBindings.Normalize("ctrl+o") = "Ctrl+O" AndAlso KeyBindings.Normalize("esc  esc") = "Esc Esc" AndAlso
              KeyBindings.Normalize("Shift+Ctrl+;") = "Ctrl+Shift+;" AndAlso KeyBindings.Normalize("f2") = "F2" AndAlso KeyBindings.Normalize("Escape") = "Esc")
        Check("keys: nonsense is not a key", KeyBindings.Normalize("Ctrl+") = "" AndAlso KeyBindings.Normalize("Hyper+X") = "" AndAlso
              KeyBindings.Normalize("a b c") = "" AndAlso KeyBindings.Normalize("Ctrl+Banana") = "")
        Check("keys: pressed keys are named", KeyBindings.Chord(New ConsoleKeyInfo("o"c, ConsoleKey.O, False, False, True)) = "Ctrl+O" AndAlso
              KeyBindings.Chord(New ConsoleKeyInfo(ChrW(9), ConsoleKey.Tab, True, False, False)) = "Shift+Tab" AndAlso
              KeyBindings.Chord(New ConsoleKeyInfo(ChrW(10), ConsoleKey.Enter, False, False, False)) = "Ctrl+J" AndAlso
              KeyBindings.Chord(New ConsoleKeyInfo("-"c, ConsoleKey.OemMinus, False, False, True)) = "Ctrl+-" AndAlso
              KeyBindings.Chord(New ConsoleKeyInfo(ChrW(27), ConsoleKey.Escape, False, False, False)) = "Esc")

        ' Changed keys are saved in the user settings (AGENTIC_HOME is this test's folder) and come back after a restart.
        Dim home = Path.Combine(tmp, "keys-home")
        Directory.CreateDirectory(home)
        Dim before = Environment.GetEnvironmentVariable("AGENTIC_HOME")
        Environment.SetEnvironmentVariable("AGENTIC_HOME", home)
        Try
            Dim proj = Path.Combine(tmp, "keys-project")
            Directory.CreateDirectory(proj)
            Dim s = Settings.Load(proj)
            KeyBindings.Load(s)
            KeyBindings.Save(s, "submit", {"Ctrl+Enter"})
            KeyBindings.Save(s, "verbose", {"F2"})
            KeyBindings.Save(s, "clear", {"Ctrl+X Ctrl+X"})
            KeyBindings.Save(s, "menu-down", {"Down", "Ctrl+N"})
            Dim saved = File.ReadAllText(Paths.UserSettingsFile())
            KeyBindings.Load(Settings.Load(proj))
            Check("keys: saved and read again after a restart", saved.Contains("""keybindings""") AndAlso KeyBindings.Describe("submit") = "Ctrl+Enter" AndAlso
                  KeyBindings.Describe("verbose") = "F2", saved.Replace(vbLf, " "))

            ' The message box follows them: Enter adds a line, Ctrl+Enter sends.
            Dim sent = Edit(Sub(t)
                                t.Type("a")
                                t.Press(ConsoleKey.Enter, ch:=ChrW(13))
                                t.Keys.Enqueue(New ConsoleKeyInfo(ChrW(0), ConsoleKey.F24, False, False, False)) ' a pause: not part of a paste
                                t.Type("b")
                                t.Press(ConsoleKey.Enter, ctrl:=True, ch:=ChrW(10))
                            End Sub).Text
            Check("keys: Enter adds a line when Send is Ctrl+Enter", sent = "a" & vbLf & "b", sent.Replace(vbLf, "⏎"))
            ' A changed pair: Ctrl+X Ctrl+X clears the box, Esc Esc does not any more.
            Dim cleared = Edit(Sub(t)
                                   t.Type("junk")
                                   t.Press(ConsoleKey.X, ctrl:=True)
                                   t.Press(ConsoleKey.X, ctrl:=True)
                                   t.Type("ok")
                                   t.Press(ConsoleKey.Enter, ctrl:=True, ch:=ChrW(10))
                               End Sub).Text
            Check("keys: a changed pair clears the box", cleared = "ok", cleared)
            Dim kept = Edit(Sub(t)
                                t.Type("keep")
                                t.Press(ConsoleKey.Escape, ch:=ChrW(27))
                                t.Press(ConsoleKey.Escape, ch:=ChrW(27))
                                t.Press(ConsoleKey.Enter, ctrl:=True, ch:=ChrW(10))
                            End Sub).Text
            Check("keys: the old Esc Esc no longer clears it", kept = "keep", kept)
            ' The session's own keys arrive for the new key (F2), not the old one.
            Dim seen As New List(Of String)
            Dim t2 As New FakeTerminal()
            t2.Press(ConsoleKey.F2)
            t2.Press(ConsoleKey.O, ctrl:=True, ch:=ChrW(15))
            t2.Press(ConsoleKey.Enter, ctrl:=True, ch:=ChrW(10))
            Dim ed As New LineEditor(t2) With {.OnKey = Function(k)
                                                            Dim hit = KeyBindings.Matches("verbose", KeyBindings.Chord(k))
                                                            If hit Then seen.Add(KeyBindings.Chord(k))
                                                            Return hit
                                                        End Function}
            ed.ReadLine()
            Check("keys: verbose follows its new key", seen.Count = 1 AndAlso seen(0) = "F2", String.Join(",", seen))
            ' Menus: Ctrl+N moves down.
            Dim mt As New FakeTerminal()
            mt.Press(ConsoleKey.N, ctrl:=True, ch:=ChrW(14))
            mt.Press(ConsoleKey.Enter, ch:=ChrW(13))
            Dim oldTerm = Menu.Term
            Dim picked As Integer
            Try
                Menu.Term = mt
                picked = Menu.Select("Pick", {New MenuOption("one"), New MenuOption("two"), New MenuOption("three")})
            Finally
                Menu.Term = oldTerm
            End Try
            Check("keys: a menu follows its changed keys", picked = 1, picked.ToString())
            ' Conflicts are found where both keys would apply.
            Check("keys: conflicts in the same place are found", KeyBindings.Conflicts("todos", "F2").Any(Function(a) a.Id = "verbose") AndAlso
                  KeyBindings.Conflicts("menu-pick", "F2").Count = 0)
            ' Back to the defaults.
            KeyBindings.ResetAll(s)
            KeyBindings.Load(Settings.Load(proj))
            Check("keys: reset all brings the defaults back", KeyBindings.Describe("submit") = "Enter" AndAlso KeyBindings.Describe("clear") = "Esc Esc" AndAlso
                  Not File.ReadAllText(Paths.UserSettingsFile()).Contains("keybindings"))
        Finally
            Environment.SetEnvironmentVariable("AGENTIC_HOME", before)
            KeyBindings.Load(Nothing)
        End Try
    End Sub

End Class
