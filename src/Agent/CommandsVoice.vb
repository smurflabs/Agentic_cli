Option Strict On
Option Explicit On
Option Infer On

Imports System.Text.Json
Imports System.Text.Json.Nodes
Imports System.Text.RegularExpressions
Imports System.Threading
Imports AgenticCli.Api
Imports AgenticCli.Core
Imports AgenticCli.Ui

Namespace Agent

    ' Speech in the CLI, with the server's voices (the web app's Read aloud and Dictation mode): /read speaks the last answer
    ' (/read on: every answer), /tldr its short spoken summary, /voice picks the voice, and /dictation talks with the chat:
    ' it listens until you pause, sends what you said, and speaks each answer's spoken summary.
    Partial Friend NotInheritable Class Session

        Private _speech As Voice
        Private _dictation As Boolean
        Private _lastMessageId As String
        ''' <summary>Answers so far (a new one is read aloud, or summarized in Dictation mode).</summary>
        Private _answerCount As Integer

        Private ReadOnly Property Speech As Voice
            Get
                If _speech Is Nothing Then _speech = New Voice(_api)
                Return _speech
            End Get
        End Property

        ''' <summary>/read on: every answer is read aloud.</summary>
        Private ReadOnly Property ReadAloudOn As Boolean
            Get
                Return State.Settings.Bool("readAloud", False)
            End Get
        End Property

        ''' <summary>The language Dictation mode hears: the dictationLanguage setting, else the terminal's (LANG), else English.</summary>
        Private Function DictationLanguage() As String
            Dim s = State.Settings.Str("dictationLanguage", Nothing)
            If Not String.IsNullOrWhiteSpace(s) Then Return s.Trim().ToLowerInvariant()
            Dim lang = If(Environment.GetEnvironmentVariable("LANG"), "")
            Return If(Regex.IsMatch(lang, "^[a-z]{2}", RegexOptions.IgnoreCase), lang.Substring(0, 2).ToLowerInvariant(), "en")
        End Function

        ''' <summary>The server's speech (voices, the user's settings), or Nothing with the reason shown.</summary>
        Private Async Function SpeechInfoAsync() As Task(Of JsonElement?)
            Try
                Dim info = Await Speech.InfoAsync(CancellationToken.None).ConfigureAwait(False)
                If Not Jx.BoolOf(info, "ready") Then
                    Out.Fail("Speech is not available: " & Jx.StrOf(info, "problem", "the speech server is not answering."))
                    Return Nothing
                End If
                Return info
            Catch ex As ApiException When ex.Status = 404
                Out.Fail("This Agentic server has no speech yet: it needs an update and a speech server (TextToSpeech).")
                Return Nothing
            Catch ex As ApiException When ex.Status = 403
                Out.Fail("Your account may not use speech (it needs the Voice permission).")
                Return Nothing
            End Try
        End Function

        ''' <summary>Keys while speech plays or the microphone listens: Esc (or the stop key) and Enter.</summary>
        Private Function WatchSpeechKeys(onStop As Action, onEnter As Action) As Func(Of Task)
            If Not State.Interactive Then Return Function() Task.CompletedTask
            Dim stopping = False
            Dim loopTask = Task.Run(Async Function()
                                        While Not stopping
                                            If Menu.InputLock.Wait(0) Then
                                                Try
                                                    While Not stopping AndAlso SafeKeyAvailable()
                                                        Dim k = Console.ReadKey(True)
                                                        If k.Key = ConsoleKey.Escape OrElse KeyBindings.Matches("stop", KeyBindings.Chord(k)) Then
                                                            onStop?.Invoke()
                                                        ElseIf k.Key = ConsoleKey.Enter Then
                                                            onEnter?.Invoke()
                                                        End If
                                                    End While
                                                Finally
                                                    Menu.InputLock.Release()
                                                End Try
                                            End If
                                            Await Task.Delay(40).ConfigureAwait(False)
                                        End While
                                    End Function)
            Return Async Function()
                       stopping = True
                       Await loopTask.ConfigureAwait(False)
                   End Function
        End Function

        ''' <summary>Speaks text in a voice (Nothing: the user's); Esc or Enter stops it. False when it was stopped or failed.</summary>
        Private Async Function SayAsync(text As String, voiceKey As String, label As String) As Task(Of Boolean)
            Dim spoken = False
            Dim failure As String = Nothing
            Using cts As New CancellationTokenSource()
                Dim stopKeys = WatchSpeechKeys(Sub() cts.Cancel(), Sub() cts.Cancel())
                Dim hint = Ansi.Muted("  Esc: stop")
                Out.StartSpinner(label & hint)
                Try
                    spoken = Await Speech.SpeakAsync(text, voiceKey, cts.Token,
                        onWaiting:=Sub(pos) Out.SpinnerText($"Waiting for the speech server: {If(pos = 1, "next in line", "number " & pos & " in line")}{hint}"),
                        onStart:=Sub() Out.SpinnerText(label & hint),
                        onBuffering:=Sub(have, need) Out.SpinnerText($"Getting the speech ready: {have:0.0} of {If(Double.IsInfinity(need), "…", need.ToString("0.0"))} s{hint}")).ConfigureAwait(False)
                Catch ex As AudioException
                    failure = ex.Message
                Catch ex As ApiException
                    failure = "Speaking failed: " & ex.Message
                End Try
                Out.StopSpinner()
                Await stopKeys().ConfigureAwait(False)
            End Using
            If failure IsNot Nothing Then Out.Fail(failure)
            Return spoken
        End Function

        Private Shared Sub ShowSpoken(text As String)
            Out.Line(Ansi.Info("  TL;DR  ") & text)
        End Sub

        ''' <summary>/read [on|off]: the last answer aloud; on / off: every answer from now on.</summary>
        Private Async Function ReadCommandAsync(arg As String) As Task
            Dim a = arg.Trim().ToLowerInvariant()
            If a = "on" OrElse a = "off" Then
                If a = "on" AndAlso Not (Await SpeechInfoAsync().ConfigureAwait(False)).HasValue Then Return
                State.Settings.SetValue("user", "readAloud", JsonValue.Create(a = "on"))
                Out.Success(If(a = "on", "Read aloud on: every answer is spoken in your voice (Esc stops it; /voice changes it)", "Read aloud off"))
                Return
            End If
            If _lastAnswer Is Nothing Then
                Out.Note("No answer to read yet.")
                Return
            End If
            If Not (Await SpeechInfoAsync().ConfigureAwait(False)).HasValue Then Return
            Await SayAsync(_lastAnswer, Nothing, "Reading aloud").ConfigureAwait(False)
        End Function

        ''' <summary>/tldr: the last answer's short spoken summary, written in the voice's personality and spoken.</summary>
        Private Async Function TldrAsync() As Task
            If _lastAnswer Is Nothing Then
                Out.Note("No answer to summarize yet.")
                Return
            End If
            Dim info = Await SpeechInfoAsync().ConfigureAwait(False)
            If Not info.HasValue Then Return
            Await SpeakSummaryAsync(DictationPrefs.From(info.Value)).ConfigureAwait(False)
        End Function

        ''' <summary>Writes the last answer's spoken summary (kept with the answer) and speaks it.</summary>
        Private Async Function SpeakSummaryAsync(prefs As DictationPrefs) As Task
            Out.StartSpinner("Writing the spoken summary…")
            Dim summary As (Text As String, Voice As String) = (Nothing, prefs.Voice)
            Dim failure As String = Nothing
            Try
                summary = Await Speech.SummaryAsync(State.ChatId, _lastMessageId, _lastAnswer, prefs.Voice, prefs.SummaryWords, CancellationToken.None).ConfigureAwait(False)
            Catch ex As ApiException
                failure = ex.Message
            End Try
            Out.StopSpinner()
            If failure IsNot Nothing Then
                Out.Fail("The spoken summary failed: " & failure)
                If prefs.SpeakErrors Then Await SayAsync("The summary could not be written. The answer is on the screen.", prefs.Voice, "Speaking").ConfigureAwait(False)
                Return
            End If
            If String.IsNullOrWhiteSpace(summary.Text) Then Return
            ShowSpoken(summary.Text)
            Await SayAsync(summary.Text, summary.Voice, "Speaking").ConfigureAwait(False)
        End Function

        ''' <summary>/voice [name]: the voice that reads answers aloud and talks in Dictation mode (no name: a menu).</summary>
        Private Async Function VoiceCommandAsync(arg As String) As Task
            Dim info = Await SpeechInfoAsync().ConfigureAwait(False)
            If Not info.HasValue Then Return
            Dim voices = Voice.Voices(info.Value)
            If voices.Count = 0 Then
                Out.Fail("The speech server has no voices.")
                Return
            End If
            Dim current = If(DictationPrefs.From(info.Value).Voice, "speaker:" & Jx.StrOf(Jx.Prop(info.Value, "defaults"), "speaker"))
            Dim q = arg.Trim()
            Dim pick As String
            If q.Length > 0 Then
                pick = voices.FirstOrDefault(Function(v) v.Equals(q, StringComparison.OrdinalIgnoreCase) OrElse Voice.VoiceName(v).Equals(q, StringComparison.OrdinalIgnoreCase))
                If pick Is Nothing Then pick = voices.FirstOrDefault(Function(v) Voice.VoiceName(v).StartsWith(q, StringComparison.OrdinalIgnoreCase))
                If pick Is Nothing Then
                    Out.Fail($"No voice is called ""{q}"": /voice lists them.")
                    Return
                End If
            Else
                Dim options = voices.Select(Function(v) New MenuOption(Voice.VoiceName(v), If(v.StartsWith("voice:", StringComparison.Ordinal), "cloned", Nothing), v)).ToList()
                Dim i = Menu.Select("The voice that reads answers aloud and talks in Dictation mode", options, Math.Max(0, voices.IndexOf(current)))
                If i < 0 Then Return
                pick = voices(i)
            End If
            Await _api.SendAsync("PUT", "/api/audio/prefs", Jx.Stringify(New With {.voice = pick}), CancellationToken.None).ConfigureAwait(False)
            Out.Success("Voice: " & Voice.VoiceName(pick))
        End Function

        ''' <summary>/dictation [on|off]: talk with the chat instead of typing.</summary>
        Private Async Function DictationCommandAsync(arg As String) As Task
            Dim a = arg.Trim().ToLowerInvariant()
            Dim turnOn = If(a = "off", False, If(a = "on", True, Not _dictation))
            If Not turnOn Then
                _dictation = False
                Out.Success("Dictation mode off")
                Return
            End If
            If Not State.Interactive Then
                Out.Fail("Dictation mode needs an interactive terminal.")
                Return
            End If
            If Not (Await SpeechInfoAsync().ConfigureAwait(False)).HasValue Then Return
            _dictation = True
            Out.Success("Dictation mode on: talk, and pause when you are done. Each answer's short summary is spoken.")
            Out.Line(Ansi.Muted("  Enter: send now · Esc: back to typing · say ""stop"" or ""that's all for now"" to end it"))
        End Function

        Private Shared Function LevelBar(level As Double) As String
            Dim n = CInt(Math.Round(Math.Clamp(level, 0, 1) * 8))
            Return New String("█"c, n) & Ansi.Muted(New String("·"c, 8 - n))
        End Function

        ''' <summary>Waits for the user after a pause: Enter talks again (True), Esc goes back to typing (False).</summary>
        Private Function WaitToTalk(note As String) As Boolean
            Out.Line(Ansi.Muted("  " & If(String.IsNullOrEmpty(note), "", note & " ") & "Enter: talk · Esc: type instead"))
            Menu.InputLock.Wait()
            Try
                While True
                    Dim k = Console.ReadKey(True)
                    If k.Key = ConsoleKey.Enter Then Return True
                    If k.Key = ConsoleKey.Escape OrElse KeyBindings.Matches("stop", KeyBindings.Chord(k)) Then Return False
                End While
            Finally
                Menu.InputLock.Release()
            End Try
            Return False
        End Function

        ''' <summary>One spoken turn of Dictation mode: what was said, or Nothing when it ended (the message box is back).</summary>
        Private Async Function DictationTurnAsync() As Task(Of String)
            While _dictation
                Dim info = Await SpeechInfoAsync().ConfigureAwait(False)
                If Not info.HasValue Then
                    _dictation = False
                    Return Nothing
                End If
                Dim prefs = DictationPrefs.From(info.Value)
                Dim sendNow = False
                Dim level = 0.0
                Dim text As String = Nothing
                Dim failure As String = Nothing
                Dim stopped = False
                Using cts As New CancellationTokenSource()
                    Dim stopKeys = WatchSpeechKeys(Sub() cts.Cancel(), Sub() sendNow = True)
                    Dim hint = Ansi.Muted("  Enter: send now · Esc: type instead")
                    Out.StartSpinner("Listening…", Function() LevelBar(level) & hint)
                    Try
                        text = Await Speech.ListenAsync(prefs, DictationLanguage(), cts.Token, Function() sendNow, Sub(l) level = l,
                                                        Sub() Out.SpinnerText("Turning speech into text…")).ConfigureAwait(False)
                    Catch ex As AudioException
                        failure = ex.Message
                    Catch ex As ApiException
                        failure = "Speech to text failed: " & ex.Message
                    End Try
                    stopped = cts.IsCancellationRequested
                    Out.StopSpinner()
                    Await stopKeys().ConfigureAwait(False)
                End Using
                If failure IsNot Nothing Then
                    Out.Fail(failure)
                    _dictation = False
                    Out.Note("Dictation mode is off.")
                    Return Nothing
                End If
                If stopped Then
                    _dictation = False
                    Out.Note("Dictation mode off: type your message (/dictation turns it on again).")
                    Return Nothing
                End If
                text = If(text, "").Trim()
                If text.Length > 0 Then
                    If DictationPrefs.IsGoodbye(text) Then
                        _dictation = False
                        Out.Success("Dictation mode off")
                        Return Nothing
                    End If
                    Return text
                End If
                If Not WaitToTalk("Nothing was heard.") Then
                    _dictation = False
                    Return Nothing
                End If
            End While
            Return Nothing
        End Function

        ''' <summary>
        ''' After a message: in Dictation mode its answer's spoken summary is spoken (or why it failed); with /read on, the
        ''' whole answer. answered: a new answer arrived.
        ''' </summary>
        Private Async Function AfterAnswerSpeechAsync(dictated As Boolean, answered As Boolean) As Task
            If Not dictated Then
                If answered AndAlso ReadAloudOn AndAlso State.Interactive Then Await SayAsync(_lastAnswer, Nothing, "Reading aloud").ConfigureAwait(False)
                Return
            End If
            If Not _dictation Then Return
            Dim info = Await SpeechInfoAsync().ConfigureAwait(False)
            If Not info.HasValue Then Return
            Dim prefs = DictationPrefs.From(info.Value)
            If answered Then
                Await SpeakSummaryAsync(prefs).ConfigureAwait(False)
            ElseIf prefs.SpeakErrors Then
                Await SayAsync("The answer failed. The error is on the screen.", prefs.Voice, "Speaking").ConfigureAwait(False)
            End If
            If _dictation AndAlso Not prefs.AutoListen AndAlso Not WaitToTalk("") Then _dictation = False
        End Function

    End Class

End Namespace
