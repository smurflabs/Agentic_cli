Option Strict On
Option Explicit On
Option Infer On

Imports System.Collections.Concurrent
Imports System.IO
Imports System.Net
Imports System.Text
Imports System.Text.Json
Imports System.Threading
Imports AgenticCli.Agent
Imports AgenticCli.Api
Imports AgenticCli.Core

''' <summary>
''' Self-tests of speech: answers are spoken as the server makes them (Read aloud), a spoken turn ends at the user's pause and
''' its recording is turned into text (Dictation mode), the spoken summary is asked for the answer, and what is read out
''' leaves the Markdown out. A fake server; AGENTIC_AUDIO_IN / AGENTIC_AUDIO_OUT stand in for the microphone and speaker.
''' </summary>
Partial Friend NotInheritable Class SelfTest

    ''' <summary>16-bit mono PCM: quiet (a little noise) and a tone, by the millisecond.</summary>
    Private Shared Function Pcm(rate As Integer, ParamArray parts As (Tone As Boolean, Ms As Integer)()) As Byte()
        Dim ms As New MemoryStream()
        Dim rnd As New Random(7)
        Dim n = 0
        For Each p In parts
            For i = 0 To rate * p.Ms \ 1000 - 1
                Dim v = If(p.Tone, CShort(Math.Round(Math.Sin(n * 440 * 2 * Math.PI / rate) * 8000)), CShort(rnd.Next(-30, 30)))
                ms.Write(BitConverter.GetBytes(v), 0, 2)
                n += 1
            Next
        Next
        Return ms.ToArray()
    End Function

    ''' <summary>Plays arrivals (wall time, speech so far) as SpeakAsync does: when it starts, and how often it ran dry.</summary>
    Private Shared Function SimulatePacing(pacer As SpeechPacer, arrivals As IList(Of (At As Double, Total As Double))) As (StartAt As Double, Dry As Integer)
        Dim queuedEnd = 0.0, pendingSec = 0.0, startAt = -1.0, dry = 0, before = 0.0
        Dim playing = False
        For Each a In arrivals
            Dim sec = a.Total - before
            before = a.Total
            If sec <= 0 Then Continue For
            pacer.Arrived(sec, a.At)
            If playing AndAlso queuedEnd < a.At + 0.05 Then
                playing = False
                dry += 1
            End If
            If playing Then
                queuedEnd = Math.Max(queuedEnd, a.At) + sec
            Else
                pendingSec += sec
                If pendingSec >= pacer.Lead(a.At) Then
                    queuedEnd = a.At + pendingSec
                    pendingSec = 0
                    playing = True
                    If startAt < 0 Then startAt = a.At
                End If
            End If
        Next
        ' The end: what is still buffered plays.
        If Not playing AndAlso startAt < 0 Then startAt = arrivals(arrivals.Count - 1).At
        Return (startAt, dry)
    End Function

    Private Shared Async Function VoiceTests(dir As String) As Task
        ' ---- units
        Check("speech: Markdown, code and links are not read out",
              Voice.Speakable("## Done" & vbLf & "- **Fixed** the `parser` ([PR](https://x.y/1))" & vbLf & "```vb" & vbLf & "Dim x = 1" & vbLf & "```" & vbLf & "See https://example.com now.") =
              "Done" & vbLf & "Fixed the parser (PR)" & vbLf & "(code)" & vbLf & "See now.", Voice.Speakable("## Done" & vbLf & "- **Fixed** the `parser` ([PR](https://x.y/1))" & vbLf & "```vb" & vbLf & "Dim x = 1" & vbLf & "```" & vbLf & "See https://example.com now."))
        Dim prefs As New DictationPrefs With {.SilenceMs = 700, .NoSpeechSeconds = 3, .MaxTurnSeconds = 20, .Sensitivity = 5}
        Dim d As New TurnDetector(prefs)
        Dim t = 0L
        Dim overAt = -1L
        For i = 0 To 200
            Dim rms = If(t < 600, 0.002, If(t < 1800, 0.2, 0.002))
            If d.Feed(rms, t) Then
                overAt = t
                Exit For
            End If
            t += 50
        Next
        Check("dictation: the turn ends at the pause after speech", d.Heard AndAlso overAt > 2400 AndAlso overAt < 2700, overAt.ToString())
        Dim quiet As New TurnDetector(prefs)
        Dim quietOver = -1L
        For tq = 0L To 5000L Step 50
            If quiet.Feed(0.002, tq) Then
                quietOver = tq
                Exit For
            End If
        Next
        Check("dictation: nothing said ends the turn after noSpeechSeconds", Not quiet.Heard AndAlso quietOver > 3000 AndAlso quietOver <= 3100, quietOver.ToString())
        Dim wav = Audio.Wav(New Byte(319) {}, 320, 16000)
        Check("audio: a WAV header for 16 kHz mono", wav.Length = 364 AndAlso Encoding.ASCII.GetString(wav, 0, 4) = "RIFF" AndAlso BitConverter.ToInt32(wav, 24) = 16000 AndAlso BitConverter.ToInt32(wav, 40) = 320)
        Dim tone = Pcm(16000, (True, 100))
        Check("audio: loudness", Audio.Rms(tone, 0, 800) > 0.15 AndAlso Audio.Rms(New Byte(1599) {}, 0, 800) = 0)

        ' ---- ending Dictation mode by voice: a stop or a goodbye on its own ends it; anything more is a message
        Dim ends = {"Stop.", "That's enough for now.", "Okay, thanks, that's all for now!", "Goodbye", "ok bye", "I'm done", "We're done for today",
                    "Stop dictation", "No, that's all.", "Good night", "That’s everything, thank you very much"}
        Dim sends = {"stop the server", "Is that all the tests?", "that is enough sugar", "thanks", "okay", "no", "stop and explain why", "good"}
        Dim wrongEnds = ends.Where(Function(s) Not DictationPrefs.IsGoodbye(s)).ToList()
        Dim wrongSends = sends.Where(Function(s) DictationPrefs.IsGoodbye(s)).ToList()
        Check("dictation: a stop or goodbye ends it", wrongEnds.Count = 0, String.Join(" | ", wrongEnds))
        Check("dictation: anything more is sent", wrongSends.Count = 0, String.Join(" | ", wrongSends))

        ' ---- pacing: speech made slower than it plays waits long enough that it never runs dry
        Check("pacing: no wait while the rate is unknown is not a start", Double.IsInfinity(SpeechPacer.LeadFor(0, 10)) AndAlso SpeechPacer.LeadFor(2, 30) = SpeechPacer.MinLead)
        Check("pacing: the wait covers the shortfall", Math.Abs(SpeechPacer.LeadFor(0.5, 10) - (SpeechPacer.MinLead + 10 * 1 * SpeechPacer.Margin)) < 0.001)
        ' The real speech server (a GTX 1660 Ti, 2026-11-02): when 23.4 s of speech (385 characters) arrived, as wall time and
        ' speech so far. The first second comes in a burst (1.2x real time), then it settles at about 0.8x.
        Dim real = New(At As Double, Total As Double)() {(1.65, 0.3), (2.8, 1.4), (3.94, 2.03), (5.08, 3.26), (6.24, 4.22), (7.4, 4.84), (8.57, 5.68), (9.77, 6.64),
            (10.97, 7.72), (12.21, 8.64), (13.45, 9.39), (14.78, 10.71), (15.29, 11.31), (16.46, 12.01), (17.63, 12.74), (18.76, 13.24), (19.88, 14.17),
            (21.04, 15.19), (22.19, 16.67), (23.36, 17.17), (24.55, 18.04), (25.76, 18.9), (27.02, 20.29), (28.34, 21.16), (29.63, 21.81), (30.86, 23.39)}
        For Each known In {False, True}
            Dim pacer As New SpeechPacer(SpeechPacer.EstimateSeconds(New String("x"c, 385), 1, If(known, 16.46, 14)), If(known, 0.8, 0.75))
            Dim r = SimulatePacing(pacer, real)
            Check($"pacing, the real server {If(known, "(its pace known)", "(the first time)")}: never runs dry", r.Dry = 0 AndAlso r.StartAt > 0 AndAlso r.StartAt < 30.86,
                  $"dry {r.Dry}, starts at {r.StartAt:0.0} s")
        Next
        ' Steady servers from much slower to faster than real time (30 s of speech, the first after 1.5 s), and a too-short estimate.
        For Each c In {(Rate:=0.5, Est:=30.0), (Rate:=0.77, Est:=30.0), (Rate:=0.95, Est:=30.0), (Rate:=1.5, Est:=30.0), (Rate:=0.77, Est:=20.0)}
            Dim steady = Enumerable.Range(1, CInt(Math.Ceiling(30 / (0.25 * c.Rate)))).Select(Function(i) (At:=1.5 + i * 0.25, Total:=Math.Min(30, i * 0.25 * c.Rate))).ToArray()
            Dim r = SimulatePacing(New SpeechPacer(c.Est, 0.75), steady)
            If c.Est = 30 Then
                Check($"pacing at {c.Rate}x real time: never runs dry", r.Dry = 0 AndAlso r.StartAt > 0, $"dry {r.Dry}, starts at {r.StartAt:0.0} s")
            Else
                Check("pacing: a too-short estimate buffers again and still ends", r.Dry <= 3 AndAlso r.StartAt > 0, $"dry {r.Dry}")
            End If
        Next
        ' What it learned from a stream is planned with next time.
        Dim learner As New SpeechPacer(30, 0.75)
        For Each x In real
            learner.Arrived(x.Total - learner.Received, x.At)
        Next
        Dim savedRate = SpeechPacer.LastRate, savedCps = SpeechPacer.LastCps
        learner.Learn(385)
        Check("pacing: the server's pace is learned", Math.Abs(SpeechPacer.LastRate - 0.785) < 0.02 AndAlso Math.Abs(SpeechPacer.LastCps - 16.46) < 0.1,
              $"{SpeechPacer.LastRate:0.000} s/s, {SpeechPacer.LastCps:0.00} chars/s")
        SpeechPacer.LastRate = savedRate
        SpeechPacer.LastCps = savedCps

        ' ---- a fake server: speech, the queue, speech to text and the spoken summary
        Dim port = FreePort()
        Dim seen As New ConcurrentQueue(Of (Path As String, Body As Byte()))
        Using listener As New HttpListener()
            listener.Prefixes.Add($"http://127.0.0.1:{port}/")
            listener.Start()
            Dim speech = Pcm(24000, (True, 3000))   ' 3 s: 144,000 bytes, made faster than it plays
            Dim serve = Task.Run(Async Function()
                                     While listener.IsListening
                                         Dim ctx As HttpListenerContext
                                         Try
                                             ctx = Await listener.GetContextAsync().ConfigureAwait(False)
                                         Catch ex As Exception When TypeOf ex Is HttpListenerException OrElse TypeOf ex Is ObjectDisposedException
                                             Exit While
                                         End Try
                                         Dim unused = Task.Run(Async Function()
                                                                   Dim body As Byte()
                                                                   Using ms As New MemoryStream()
                                                                       Await ctx.Request.InputStream.CopyToAsync(ms).ConfigureAwait(False)
                                                                       body = ms.ToArray()
                                                                   End Using
                                                                   Dim p = ctx.Request.Url.PathAndQuery
                                                                   seen.Enqueue((p, body))
                                                                   If p.StartsWith("/api/audio/stream", StringComparison.Ordinal) Then
                                                                       ctx.Response.ContentType = "application/octet-stream"
                                                                       ctx.Response.SendChunked = True
                                                                       ' An odd split: samples cross the chunks.
                                                                       For at = 0 To speech.Length - 1 Step 9601
                                                                           Dim n = Math.Min(9601, speech.Length - at)
                                                                           Try
                                                                               Await ctx.Response.OutputStream.WriteAsync(speech, at, n).ConfigureAwait(False)
                                                                               Await ctx.Response.OutputStream.FlushAsync().ConfigureAwait(False)
                                                                           Catch ex As Exception When TypeOf ex Is HttpListenerException OrElse TypeOf ex Is IOException
                                                                               Return
                                                                           End Try
                                                                           Await Task.Delay(150).ConfigureAwait(False)
                                                                       Next
                                                                       ctx.Response.Close()
                                                                   ElseIf p.StartsWith("/api/audio/queue", StringComparison.Ordinal) Then
                                                                       Await ReplyAsync(ctx, "{""slots"":2,""running"":2,""waiting"":0,""mine"":[]}").ConfigureAwait(False)
                                                                   ElseIf p.StartsWith("/api/chat/transcribe", StringComparison.Ordinal) Then
                                                                       Await ReplyAsync(ctx, "{""text"":"" turn on the lights ""}").ConfigureAwait(False)
                                                                   ElseIf p.StartsWith("/api/audio/summary", StringComparison.Ordinal) Then
                                                                       Await ReplyAsync(ctx, "{""text"":""Short summary."",""voice"":""speaker:Ana Florence""}").ConfigureAwait(False)
                                                                   ElseIf p = "/api/audio" Then
                                                                       Await ReplyAsync(ctx, "{""ready"":true,""prefs"":{""silenceMs"":700,""voice"":""voice:mine"",""summaryWords"":40},""defaults"":{""speaker"":""Ana Florence"",""summaryWords"":60}," &
                                                                                             """speakers"":{""builtin"":[""Ana Florence"",""Damien Black""],""voices"":[""mine""]}}").ConfigureAwait(False)
                                                                   Else
                                                                       ctx.Response.StatusCode = 404
                                                                       ctx.Response.Close()
                                                                   End If
                                                               End Function)
                                     End While
                                 End Function)
            Dim outFile = Path.Combine(dir, "spoken.pcm")
            Dim micFile = Path.Combine(dir, "mic.wav")
            Dim micPcm = Pcm(16000, (False, 600), (True, 1200), (False, 400))
            File.WriteAllBytes(micFile, Audio.Wav(micPcm, micPcm.Length, 16000))
            Environment.SetEnvironmentVariable("AGENTIC_AUDIO_OUT", outFile)
            Environment.SetEnvironmentVariable("AGENTIC_AUDIO_IN", micFile)
            Try
                Using api As New ApiClient($"http://127.0.0.1:{port}", "agk_test")
                    Dim v As New Voice(api)
                    Dim info = Await v.InfoAsync(CancellationToken.None).ConfigureAwait(False)
                    Dim p = DictationPrefs.From(info)
                    Check("speech: the user's settings and voices", p.SilenceMs = 700 AndAlso p.Voice = "voice:mine" AndAlso p.SummaryWords = 40 AndAlso
                          String.Join(",", Voice.Voices(info)) = "voice:mine,speaker:Ana Florence,speaker:Damien Black", String.Join(",", Voice.Voices(info)))

                    ' Read aloud: the speech plays as it arrives, whole samples only.
                    Dim startedAfter = -1L
                    Dim clock = Diagnostics.Stopwatch.StartNew()
                    Dim spoke = Await v.SpeakAsync("**Hello** there", "speaker:Damien Black", CancellationToken.None, onStart:=Sub() startedAfter = clock.ElapsedMilliseconds).ConfigureAwait(False)
                    Dim played = File.ReadAllBytes(outFile)
                    Check("read aloud: all of the speech is played", spoke AndAlso played.SequenceEqual(speech), played.Length.ToString())
                    Check("read aloud: it starts before the speech is all made", startedAfter >= 0 AndAlso startedAfter < clock.ElapsedMilliseconds - 400, $"{startedAfter} of {clock.ElapsedMilliseconds} ms")
                    Dim sent = Jx.Parse(Encoding.UTF8.GetString(seen.Last(Function(x) x.Path.StartsWith("/api/audio/stream", StringComparison.Ordinal)).Body))
                    Check("read aloud: the text without Markdown, in the voice", Jx.StrOf(sent, "text") = "Hello there" AndAlso Jx.StrOf(sent, "voiceKey") = "speaker:Damien Black", sent.GetRawText())
                    ' Esc stops it.
                    File.Delete(outFile)
                    Using cts As New CancellationTokenSource(250)
                        Dim stoppedEarly = Not Await v.SpeakAsync("Stop me", Nothing, cts.Token).ConfigureAwait(False)
                        Check("read aloud: Esc stops it", stoppedEarly AndAlso (Not File.Exists(outFile) OrElse New FileInfo(outFile).Length < speech.Length))
                    End Using

                    ' Dictation mode: the turn ends at the pause, and its recording is turned into text.
                    clock.Restart()
                    Dim heard = Await v.ListenAsync(p, "en", CancellationToken.None).ConfigureAwait(False)
                    Dim took = clock.ElapsedMilliseconds
                    Check("dictation: what was said, as text", heard = "turn on the lights", heard)
                    Check("dictation: it stops at the pause (not the 2-minute limit)", took > 2000 AndAlso took < 4500, took & " ms")
                    Dim up = seen.Last(Function(x) x.Path.StartsWith("/api/chat/transcribe", StringComparison.Ordinal))
                    Dim seconds = (up.Body.Length - 44) / 2.0 / 16000
                    Check("dictation: a WAV of the turn goes to the server", up.Path = "/api/chat/transcribe?name=speech.wav&language=en" AndAlso
                          Encoding.ASCII.GetString(up.Body, 0, 4) = "RIFF" AndAlso seconds > 2 AndAlso seconds < 3.2, $"{up.Path} {seconds:0.00} s")
                    ' Enter sends what was heard at once; Esc throws the turn away.
                    clock.Restart()
                    Dim early = Await v.ListenAsync(p, "en", CancellationToken.None, sendNow:=Function() clock.ElapsedMilliseconds > 1000).ConfigureAwait(False)
                    Check("dictation: Enter sends at once", early = "turn on the lights" AndAlso clock.ElapsedMilliseconds < 1800, clock.ElapsedMilliseconds & " ms")
                    Dim before = seen.Where(Function(x) x.Path.StartsWith("/api/chat/transcribe", StringComparison.Ordinal)).Count()
                    Using cts As New CancellationTokenSource(500)
                        Dim gone = Await v.ListenAsync(p, "en", cts.Token).ConfigureAwait(False)
                        Check("dictation: Esc throws the turn away", gone = "" AndAlso seen.Where(Function(x) x.Path.StartsWith("/api/chat/transcribe", StringComparison.Ordinal)).Count() = before)
                    End Using

                    ' The spoken summary is asked for the answer (kept with it), or for its text when it has no id.
                    Dim s = Await v.SummaryAsync("chat1", "msg1", "The answer.", "voice:mine", 40, CancellationToken.None).ConfigureAwait(False)
                    Dim asked = Jx.Parse(Encoding.UTF8.GetString(seen.Last(Function(x) x.Path.StartsWith("/api/audio/summary", StringComparison.Ordinal)).Body))
                    Check("tl;dr: asked for the answer in the voice", s.Text = "Short summary." AndAlso Jx.StrOf(asked, "chatId") = "chat1" AndAlso Jx.StrOf(asked, "messageId") = "msg1" AndAlso
                          Jx.IntOf(asked, "words") = 40 AndAlso Not Jx.Has(asked, "text"), asked.GetRawText())
                    Await v.SummaryAsync(Nothing, Nothing, "The answer.", Nothing, 40, CancellationToken.None).ConfigureAwait(False)
                    asked = Jx.Parse(Encoding.UTF8.GetString(seen.Last(Function(x) x.Path.StartsWith("/api/audio/summary", StringComparison.Ordinal)).Body))
                    Check("tl;dr: an answer without an id is sent as text", Jx.StrOf(asked, "text") = "The answer." AndAlso Not Jx.Has(asked, "chatId"))
                End Using
            Finally
                Environment.SetEnvironmentVariable("AGENTIC_AUDIO_OUT", Nothing)
                Environment.SetEnvironmentVariable("AGENTIC_AUDIO_IN", Nothing)
                listener.Stop()
            End Try
            Await serve.ConfigureAwait(False)
        End Using

        ' The real speaker and microphone: they open, or say why not (a machine without sound).
        If OperatingSystem.IsWindows() Then
            Dim said As String = Nothing
            Try
                Audio.OpenSpeaker(Audio.SpeechRate).Dispose()
            Catch ex As AudioException
                said = ex.Message
            End Try
            Check("audio: the Windows speaker opens, or says why not", said Is Nothing OrElse said.Contains("waveOutOpen"), said)
            said = Nothing
            Try
                Audio.OpenMicrophone(Audio.MicRate, Sub(b, n) Return).Dispose()
            Catch ex As AudioException
                said = ex.Message
            End Try
            Check("audio: the Windows microphone opens, or says why not", said Is Nothing OrElse said.Contains("waveInOpen"), said)
        End If
    End Function
End Class
