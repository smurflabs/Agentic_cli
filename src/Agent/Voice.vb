Option Strict On
Option Explicit On
Option Infer On

Imports System.Net.Http
Imports System.Text
Imports System.Text.Json
Imports System.Text.RegularExpressions
Imports System.Threading
Imports AgenticCli.Api
Imports AgenticCli.Core

Namespace Agent

    ''' <summary>The user's Dictation mode settings (the web app's Audio → Dictation, from GET /api/audio).</summary>
    Friend NotInheritable Class DictationPrefs
        Public Property SilenceMs As Integer = 1400
        Public Property MaxTurnSeconds As Integer = 120
        Public Property NoSpeechSeconds As Integer = 30
        Public Property Sensitivity As Integer = 5
        Public Property SummaryWords As Integer = 60
        Public Property AutoListen As Boolean = True
        Public Property SpeakErrors As Boolean = True
        ''' <summary>The chosen voice (speaker:Name or voice:name), Nothing for the server's default.</summary>
        Public Property Voice As String

        Public Shared Function From(info As JsonElement) As DictationPrefs
            Dim p As New DictationPrefs()
            Dim prefs = Jx.Prop(info, "prefs")
            Dim defaults = Jx.Prop(info, "defaults")
            If prefs.ValueKind = JsonValueKind.Object Then
                p.SilenceMs = Jx.IntOf(prefs, "silenceMs", p.SilenceMs)
                p.MaxTurnSeconds = Jx.IntOf(prefs, "maxTurnSeconds", p.MaxTurnSeconds)
                p.NoSpeechSeconds = Jx.IntOf(prefs, "noSpeechSeconds", p.NoSpeechSeconds)
                p.Sensitivity = Jx.IntOf(prefs, "sensitivity", p.Sensitivity)
                p.SummaryWords = Jx.IntOf(prefs, "summaryWords", If(defaults.ValueKind = JsonValueKind.Object, Jx.IntOf(defaults, "summaryWords", p.SummaryWords), p.SummaryWords))
                p.AutoListen = Jx.BoolOf(prefs, "autoListen", True)
                p.SpeakErrors = Jx.BoolOf(prefs, "speakErrors", True)
                Dim v = Jx.StrOf(prefs, "voice")
                p.Voice = If(String.IsNullOrWhiteSpace(v), Nothing, v)
            End If
            Return p
        End Function

        ''' <summary>What ends Dictation mode when said on its own: a stop, or a goodbye (the web app's list).</summary>
        Private Shared ReadOnly EndPhrases As New HashSet(Of String)(StringComparer.Ordinal) From {
            "stop", "stop listening", "stop dictation", "stop dictation mode", "stop voice mode", "stop talking", "stop here", "lets stop", "lets stop here",
            "we can stop", "you can stop", "end", "end dictation", "end dictation mode", "end voice mode", "end listening", "end the conversation",
            "end conversation", "end the chat", "end chat", "exit", "exit dictation", "quit", "quit dictation", "done", "im done", "i am done", "were done",
            "we are done", "all done", "finished", "im finished", "i am finished", "thats enough", "that is enough", "enough", "thats all", "that is all",
            "thats it", "that is it", "thatll be all", "that will be all", "thats everything", "nothing else", "nothing more", "no more", "goodbye",
            "good bye", "bye", "bye bye", "see you", "see you later", "see ya", "talk later", "talk to you later", "talk soon", "good night", "goodnight",
            "signing off", "over and out", "lets leave it there", "leave it there", "lets call it a day", "call it a day", "lets wrap up", "lets end here"}
        Private Shared ReadOnly EndLead As String() = {"thank you very much", "thank you so much", "thanks a lot", "thank you", "thanks", "all right", "alright", "okay", "ok",
            "right", "well", "so", "great", "good", "perfect", "cool", "nice", "awesome", "cheers", "nope", "nah", "no", "yeah", "yes", "please", "hey", "um", "uh", "hmm", "and"}
        Private Shared ReadOnly EndTail As String() = {"thank you very much", "thank you so much", "thanks a lot", "for the day", "for tonight", "for today", "for now", "for me",
            "thank you", "thanks", "goodbye", "bye", "please", "then", "now", "here"}

        ''' <summary>
        ''' "Stop", "that's enough for now", "thanks, that's all", "goodbye": the user is done talking, so Dictation mode ends and
        ''' nothing is sent. Polite words around it do not count; anything more ("stop the server") is a message.
        ''' </summary>
        Public Shared Function IsGoodbye(said As String) As Boolean
            Dim t = " " & Regex.Replace(Regex.Replace(If(said, "").ToLowerInvariant(), "['’]", ""), "[^a-z0-9]+", " ").Trim() & " "
            Dim changed = True
            While changed
                If EndPhrases.Contains(t.Trim()) Then Return True
                changed = False
                For Each w In EndLead
                    If t.StartsWith(" " & w & " ", StringComparison.Ordinal) AndAlso t.Trim() <> w Then t = t.Substring(w.Length + 1) : changed = True : Exit For
                Next
                If changed Then Continue While
                For Each w In EndTail
                    If t.EndsWith(" " & w & " ", StringComparison.Ordinal) AndAlso t.Trim() <> w Then t = t.Substring(0, t.Length - w.Length - 1) : changed = True : Exit For
                Next
            End While
            Return False
        End Function
    End Class

    ''' <summary>
    ''' Where a spoken turn ends, by the web app's rule: loudness is measured every 50 ms against the room's noise (learned
    ''' while nobody speaks; the louder than it by the sensitivity's factor is speech), and SilenceMs of quiet after speech
    ''' ends the turn; nothing said for NoSpeechSeconds, or a turn of MaxTurnSeconds, ends it too.
    ''' </summary>
    Friend NotInheritable Class TurnDetector
        Private ReadOnly _p As DictationPrefs
        Private _floor As Double = 0.005
        Private _lastVoiceMs As Long
        Public Property Heard As Boolean
        ''' <summary>The last frame's loudness against the threshold, 0 to 1 (for a level meter).</summary>
        Public Property Level As Double

        Public Sub New(prefs As DictationPrefs)
            _p = prefs
        End Sub

        ''' <summary>One 50 ms frame's loudness at nowMs (from the turn's start); True when the turn is over.</summary>
        Public Function Feed(rms As Double, nowMs As Long) As Boolean
            Dim s = Math.Clamp(_p.Sensitivity, 1, 10)
            Dim factor = 1.5 + (10 - s) * 0.35, floorMin = 0.004 + (10 - s) * 0.002
            Dim threshold = Math.Max(floorMin, _floor * factor)
            Level = Math.Clamp(rms / (threshold * 2), 0, 1)
            If rms > threshold Then
                Heard = True
                _lastVoiceMs = nowMs
            ElseIf Not Heard Then
                _floor = Math.Min(0.05, _floor * 0.95 + rms * 0.05)
            End If
            Return Over(nowMs)
        End Function

        Public Function Over(nowMs As Long) As Boolean
            If Heard AndAlso nowMs - _lastVoiceMs > _p.SilenceMs Then Return True
            If Not Heard AndAlso nowMs > _p.NoSpeechSeconds * 1000L Then Return True
            Return nowMs > _p.MaxTurnSeconds * 1000L
        End Function
    End Class

    ''' <summary>
    ''' When streamed speech may play without catching up with the server (the web app's rule). A GPU may make speech slower
    ''' than it plays (rate &lt; 1: seconds made per second); the rest (remaining seconds) then arrives remaining / rate seconds
    ''' from now but plays in remaining seconds, so the buffer must cover the difference, remaining × (1 / rate − 1), plus a
    ''' margin. The rate planned with is the slowest of the server's rate last time (a cautious 0.75 until known), the average
    ''' since the first sound and the last 3 s; the first second comes in a burst, so the measured ones count only after 2 s.
    ''' The length is estimated from the text at the speaking pace measured last time (14 characters a second until known).
    ''' </summary>
    Friend NotInheritable Class SpeechPacer
        Public Const MinLead As Double = 1
        Public Const Margin As Double = 1.35
        ''' <summary>How the speech server did last time in this session: seconds made per second, characters per second at speed 1.</summary>
        Public Shared Property LastRate As Double = 0.75
        Public Shared Property LastCps As Double = 14
        Private ReadOnly _estimate As Double
        Private ReadOnly _prior As Double
        Private _firstAt As Double = -1
        Private _firstSec As Double
        Private _lastAt As Double
        Private ReadOnly _arrivals As New List(Of (At As Double, Received As Double))
        Public Property Received As Double

        Public Sub New(estimatedSeconds As Double, Optional prior As Double = 0)
            _estimate = estimatedSeconds
            _prior = If(prior > 0, prior, LastRate)
        End Sub

        Public Shared Function EstimateSeconds(text As String, speed As Double, Optional cps As Double = 0) As Double
            Return If(text, "").Length / If(cps > 0, cps, LastCps) / If(speed > 0, speed, 1.0)
        End Function

        ''' <summary>Remembers how the server did (speech of chars characters at speed 1), when it was long enough to tell.</summary>
        Public Sub Learn(chars As Integer)
            Dim took = _lastAt - _firstAt
            If Received < 3 OrElse took < 2 OrElse chars <= 0 Then Return
            LastRate = (Received - _firstSec) / took
            LastCps = chars / Received
        End Sub

        ''' <summary>The buffer needed for the rest at a rate; Infinity while the rate is unknown.</summary>
        Public Shared Function LeadFor(rate As Double, remaining As Double) As Double
            If Not (rate > 0) Then Return Double.PositiveInfinity
            If rate >= 1.15 Then Return MinLead
            Return MinLead + Math.Max(0, remaining) * (1 / rate - 1) * Margin
        End Function

        ''' <summary>Speech that arrived (seconds of it) at nowSec.</summary>
        Public Sub Arrived(seconds As Double, nowSec As Double)
            Received += seconds
            If _firstAt < 0 Then
                _firstAt = nowSec
                _firstSec = Received
            End If
            _lastAt = nowSec
            _arrivals.Add((nowSec, Received))
            _arrivals.RemoveAll(Function(a) nowSec - a.At > 4)
        End Sub

        ''' <summary>The rate to plan with (seconds of speech made per second): see the class.</summary>
        Public Function Rate(nowSec As Double) As Double
            Dim r = _prior
            Dim elapsed = nowSec - _firstAt
            If _firstAt < 0 OrElse elapsed < 2 Then Return r
            r = Math.Min(r, (Received - _firstSec) / elapsed)
            Dim old = _arrivals.FirstOrDefault(Function(a) nowSec - a.At <= 3)
            If _arrivals.Count > 0 AndAlso nowSec - old.At >= 1.5 Then r = Math.Min(r, (Received - old.Received) / (nowSec - old.At))
            Return r
        End Function

        ''' <summary>How much must be buffered before playing now.</summary>
        Public Function Lead(nowSec As Double) As Double
            Dim remaining = If(Received < _estimate, _estimate - Received, Math.Max(1, Received * 0.1))
            Return LeadFor(Rate(nowSec), remaining)
        End Function
    End Class

    ''' <summary>
    ''' Speech from the Agentic server for the CLI: Read aloud and Dictation mode's spoken summaries play as the server makes
    ''' them (/api/audio/stream, raw PCM), a spoken turn is recorded until the user pauses and turned into text
    ''' (/api/chat/transcribe), and the spoken summary of an answer is written in the voice's personality (/api/audio/summary).
    ''' </summary>
    Friend NotInheritable Class Voice
        Private ReadOnly _api As ApiClient

        Public Sub New(api As ApiClient)
            _api = api
        End Sub

        ''' <summary>GET /api/audio: whether speech works (ready, problem), the voices, languages and the user's settings.</summary>
        Public Function InfoAsync(ct As CancellationToken) As Task(Of JsonElement)
            Return _api.GetAsync("/api/audio", ct, 30)
        End Function

        ''' <summary>Every voice: the cloned ones (voice:name), then the built-in speakers (speaker:Name).</summary>
        Public Shared Function Voices(info As JsonElement) As List(Of String)
            Dim list As New List(Of String)
            Dim sp = Jx.Prop(info, "speakers")
            If sp.ValueKind <> JsonValueKind.Object Then Return list
            For Each v In Jx.ArrOf(sp, "voices")
                If v.ValueKind = JsonValueKind.String Then list.Add("voice:" & v.GetString())
            Next
            For Each v In Jx.ArrOf(sp, "builtin")
                If v.ValueKind = JsonValueKind.String Then list.Add("speaker:" & v.GetString())
            Next
            Return list
        End Function

        Public Shared Function VoiceName(key As String) As String
            If String.IsNullOrEmpty(key) Then Return ""
            Dim at = key.IndexOf(":"c)
            Return If(at >= 0, key.Substring(at + 1), key)
        End Function

        ''' <summary>
        ''' Speaks text in a voice (Nothing: the user's), playing it as it is made; Esc (ct) stops it. The server's GPU may make
        ''' speech slower than it plays, so the first sound waits until enough is buffered for the rest to arrive in time
        ''' (SpeechPacer); should it run dry anyway, it buffers again. onWaiting(position) tells while the speech server is busy
        ''' with other people's requests; onBuffering(buffered, needed) while it buffers. False when it was stopped.
        ''' </summary>
        Public Async Function SpeakAsync(text As String, voiceKey As String, ct As CancellationToken, Optional onWaiting As Action(Of Integer) = Nothing,
                                         Optional onStart As Action = Nothing, Optional onBuffering As Action(Of Double, Double) = Nothing) As Task(Of Boolean)
            Dim speech = Speakable(text)
            If speech.Length = 0 Then Return True
            Dim body = Jx.Stringify(New Dictionary(Of String, Object) From {{"text", speech}, {"voiceKey", voiceKey}})
            ' While the answer has not started: the speech server makes one at a time, the rest wait in line.
            Dim started = False
            Dim watcher = Task.Run(Async Function()
                                       Try
                                           Await Task.Delay(700, ct).ConfigureAwait(False)
                                           While Not started AndAlso Not ct.IsCancellationRequested
                                               Dim q = Await _api.GetAsync("/api/audio/queue", ct, 10).ConfigureAwait(False)
                                               Dim mine = Jx.ArrOf(q, "mine")
                                               If mine.Count > 0 AndAlso Not started Then onWaiting?.Invoke(Jx.IntOf(mine(0), "position", 1))
                                               Await Task.Delay(1500, ct).ConfigureAwait(False)
                                           End While
                                       Catch ex As Exception When TypeOf ex Is OperationCanceledException OrElse TypeOf ex Is ApiException
                                       End Try
                                   End Function)
            Using speaker = Audio.OpenSpeaker(Audio.SpeechRate)
                Dim resp As HttpResponseMessage
                Try
                    resp = Await _api.OpenStreamAsync("/api/audio/stream?format=pcm", body, ct, "application/octet-stream").ConfigureAwait(False)
                Catch ex As OperationCanceledException
                    Return False
                Finally
                    started = True
                End Try
                Using resp
                    Dim buf(9599) As Byte
                    Dim carry = -1
                    Dim first = True
                    Dim stopped = False
                    Dim pacer As New SpeechPacer(SpeechPacer.EstimateSeconds(speech, 1))
                    Dim clock = Diagnostics.Stopwatch.StartNew()
                    Dim pending As New List(Of Byte())
                    Dim pendingSec = 0.0, playing = False, queuedEnd = 0.0
                    ' Plays what was buffered; from now on what arrives plays at once (until it runs dry).
                    Dim release = Sub()
                                      For Each p In pending
                                          speaker.Write(p, p.Length)
                                      Next
                                      queuedEnd = clock.Elapsed.TotalSeconds + pendingSec
                                      pending.Clear()
                                      pendingSec = 0
                                      playing = True
                                      If first Then
                                          first = False
                                          onStart?.Invoke()
                                      End If
                                  End Sub
                    Try
                        Using s = Await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(False)
                            While True
                                Dim n = Await s.ReadAsync(buf.AsMemory(If(carry >= 0, 1, 0)), ct).ConfigureAwait(False)
                                If n <= 0 Then Exit While
                                If carry >= 0 Then
                                    buf(0) = CByte(carry)
                                    n += 1
                                    carry = -1
                                End If
                                ' Whole samples only (2 bytes each).
                                Dim even = n - (n Mod 2)
                                If even < n Then carry = buf(n - 1)
                                If even = 0 Then Continue While
                                Dim pcm = buf.AsSpan(0, even).ToArray()
                                Dim sec = even / 2.0 / Audio.SpeechRate
                                Dim nowSec = clock.Elapsed.TotalSeconds
                                pacer.Arrived(sec, nowSec)
                                ' It caught up with the server after all: it buffers again before going on.
                                If playing AndAlso queuedEnd < nowSec + 0.05 Then playing = False
                                If playing Then
                                    speaker.Write(pcm, pcm.Length)
                                    queuedEnd = Math.Max(queuedEnd, nowSec) + sec
                                Else
                                    pending.Add(pcm)
                                    pendingSec += sec
                                    Dim needed = pacer.Lead(nowSec)
                                    If pendingSec >= needed Then release() Else onBuffering?.Invoke(pendingSec, needed)
                                End If
                            End While
                        End Using
                        ' Everything has arrived: what is still buffered plays now; how the server did is kept for next time.
                        If pending.Count > 0 Then release()
                        pacer.Learn(speech.Length)
                        Await speaker.DrainAsync(ct).ConfigureAwait(False)
                    Catch ex As OperationCanceledException
                        stopped = True
                    End Try
                    ' Stopped (Esc): what was queued stops at once.
                    If stopped Then Await speaker.DrainAsync(New CancellationToken(True)).ConfigureAwait(False)
                End Using
            End Using
            Await watcher.ConfigureAwait(False)
            Return Not ct.IsCancellationRequested
        End Function

        ''' <summary>
        ''' Records one spoken turn and turns it into text ("" when nothing was said). sendNow ends it now with what was heard;
        ''' ct throws it away. onLevel gets the loudness; onTranscribing, when it is turned into text.
        ''' </summary>
        Public Async Function ListenAsync(prefs As DictationPrefs, language As String, ct As CancellationToken, Optional sendNow As Func(Of Boolean) = Nothing,
                                          Optional onLevel As Action(Of Double) = Nothing, Optional onTranscribing As Action = Nothing) As Task(Of String)
            Dim detector As New TurnDetector(prefs)
            Dim recorded As New IO.MemoryStream()
            Dim frame = Audio.MicRate \ 20 * 2   ' 50 ms
            Dim carry As New IO.MemoryStream()
            Dim done As New TaskCompletionSource(Of Boolean)(TaskCreationOptions.RunContinuationsAsynchronously)
            Dim clock = Diagnostics.Stopwatch.StartNew()
            Dim gate As New Object()
            Dim onAudio = Sub(b As Byte(), n As Integer)
                              SyncLock gate
                                  If done.Task.IsCompleted Then Return
                                  carry.Write(b, 0, n)
                                  Dim data = carry.ToArray()
                                  Dim at = 0
                                  While data.Length - at >= frame
                                      recorded.Write(data, at, frame)
                                      Dim over = detector.Feed(Audio.Rms(data, at, frame \ 2), clock.ElapsedMilliseconds)
                                      at += frame
                                      onLevel?.Invoke(detector.Level)
                                      If over Then
                                          done.TrySetResult(True)
                                          Exit While
                                      End If
                                  End While
                                  carry.SetLength(0)
                                  carry.Write(data, at, data.Length - at)
                              End SyncLock
                          End Sub
            Dim mic = Audio.OpenMicrophone(Audio.MicRate, onAudio)
            Try
                ' Limits hold even when the microphone sends nothing; Enter (sendNow) ends the turn at once.
                While Not done.Task.IsCompleted
                    If ct.IsCancellationRequested Then Return ""
                    If mic.Problem IsNot Nothing Then Throw New AudioException(mic.Problem)
                    If sendNow IsNot Nothing AndAlso sendNow() Then
                        SyncLock gate
                            If recorded.Length > 0 Then detector.Heard = True
                            done.TrySetResult(True)
                        End SyncLock
                        Exit While
                    End If
                    SyncLock gate
                        If detector.Over(clock.ElapsedMilliseconds) Then done.TrySetResult(True)
                    End SyncLock
                    Await Task.WhenAny(done.Task, Task.Delay(50)).ConfigureAwait(False)
                End While
            Finally
                mic.Dispose()
            End Try
            Dim pcm As Byte()
            SyncLock gate
                If Not detector.Heard Then Return ""
                pcm = recorded.ToArray()
            End SyncLock
            If pcm.Length < Audio.MicRate \ 5 Then Return ""
            onTranscribing?.Invoke()
            Dim wav = Audio.Wav(pcm, pcm.Length, Audio.MicRate)
            Dim r = Await _api.PostBytesAsync("/api/chat/transcribe?name=speech.wav&language=" & Uri.EscapeDataString(If(language, "en")), wav, ct).ConfigureAwait(False)
            Return Jx.StrOf(r, "text").Trim()
        End Function

        ''' <summary>The spoken summary (TL;DR) of an answer, in the voice's personality: kept with the answer when it has an id.</summary>
        Public Async Function SummaryAsync(chatId As String, messageId As String, text As String, voiceKey As String, words As Integer, ct As CancellationToken) As Task(Of (Text As String, Voice As String))
            Dim body As New Dictionary(Of String, Object) From {{"voiceKey", voiceKey}, {"words", words}}
            If Not String.IsNullOrEmpty(chatId) AndAlso Not String.IsNullOrEmpty(messageId) Then
                body("chatId") = chatId
                body("messageId") = messageId
            Else
                body("text") = text
            End If
            Dim r = Await _api.PostAsync("/api/audio/summary", Jx.Stringify(body), ct, 180).ConfigureAwait(False)
            Return (Jx.StrOf(r, "text"), Jx.StrOf(r, "voice", voiceKey))
        End Function

        ''' <summary>An answer as it should sound: code, links, tables and Markdown marks are not read out.</summary>
        Public Shared Function Speakable(markdown As String) As String
            Dim s = If(markdown, "").Replace(ChrW(13), "")
            s = Regex.Replace(s, "```[\s\S]*?(```|$)", " (code) ")
            s = Regex.Replace(s, "<think>[\s\S]*?</think>", " ")
            s = Regex.Replace(s, "!\[[^\]]*\]\([^)]*\)", " ")
            s = Regex.Replace(s, "\[([^\]]+)\]\([^)]*\)", "$1")
            s = Regex.Replace(s, "https?://\S+", " ")
            s = Regex.Replace(s, "`([^`]*)`", "$1")
            s = Regex.Replace(s, "(?m)^\s{0,3}#{1,6}\s*", "")
            s = Regex.Replace(s, "(?m)^\s*([-*+]|\d+[.)])\s+", "")
            s = Regex.Replace(s, "(?m)^\s*>\s?", "")
            s = Regex.Replace(s, "(?m)^\s*\|?\s*:?-{3,}.*$", "")
            s = s.Replace("|", ", ")
            s = Regex.Replace(s, "(\*\*|__|\*|_|~~)(\S[\s\S]*?\S|\S)\1", "$2")
            s = Regex.Replace(s, "[ \t]+", " ")
            s = Regex.Replace(s, "(?m)^ +| +$", "")
            s = Regex.Replace(s, "\n{2,}", vbLf)
            Return s.Trim()
        End Function
    End Class

End Namespace
