Option Strict On
Option Explicit On
Option Infer On

Imports System.ComponentModel
Imports System.IO
Imports System.Runtime.InteropServices
Imports System.Threading

Namespace Core

    ''' <summary>The speaker or microphone cannot be used; the message says what to do.</summary>
    Friend Class AudioException
        Inherits Exception

        Public Sub New(message As String)
            MyBase.New(message)
        End Sub
    End Class

    ''' <summary>Plays 16-bit mono PCM as it arrives (Read aloud, Dictation mode's spoken summaries).</summary>
    Friend Interface ISpeaker
        Inherits IDisposable
        ''' <summary>Queues samples to play.</summary>
        Sub Write(pcm As Byte(), count As Integer)
        ''' <summary>Waits until everything queued has played; ct stops it at once.</summary>
        Function DrainAsync(ct As CancellationToken) As Task
    End Interface

    ''' <summary>Records 16-bit mono PCM (Dictation mode) until disposed; the samples go to the handler on a background thread.</summary>
    Friend Interface IMicrophone
        Inherits IDisposable
        ''' <summary>Why recording stopped on its own (no microphone, the recorder ended), else Nothing.</summary>
        ReadOnly Property Problem As String
    End Interface

    ''' <summary>
    ''' Sound for Read aloud and Dictation mode, with nothing to install on Windows (the waveOut / waveIn API). Linux uses
    ''' paplay or aplay to play and parecord or arecord to record (sox or ffmpeg also work); macOS sox or ffmpeg. For tests
    ''' (or a machine without sound), AGENTIC_AUDIO_OUT names a file the speech is written to instead, and AGENTIC_AUDIO_IN a
    ''' WAV file that is heard as the microphone (in real time, then silence).
    ''' </summary>
    Friend NotInheritable Class Audio
        ''' <summary>The speech server's audio.</summary>
        Public Const SpeechRate As Integer = 24000
        ''' <summary>What the microphone records (speech-to-text works on 16 kHz).</summary>
        Public Const MicRate As Integer = 16000

        Private Sub New()
        End Sub

        Public Shared Function OpenSpeaker(rate As Integer) As ISpeaker
            Dim file = Environment.GetEnvironmentVariable("AGENTIC_AUDIO_OUT")
            If Not String.IsNullOrWhiteSpace(file) Then Return New FileSpeaker(file)
            If OperatingSystem.IsWindows() Then Return New WinSpeaker(rate)
            Dim r = rate.ToString(Globalization.CultureInfo.InvariantCulture)
            Dim candidates As New List(Of (Cmd As String, Args As String))
            If Not OperatingSystem.IsMacOS() Then
                candidates.Add(("paplay", $"--raw --format=s16le --rate={r} --channels=1"))
                candidates.Add(("aplay", $"-q -f S16_LE -r {r} -c 1 -t raw"))
            End If
            candidates.Add(("play", $"-q -t raw -r {r} -e signed -b 16 -c 1 -"))
            candidates.Add(("ffplay", $"-nodisp -autoexit -loglevel error -f s16le -ar {r} -ac 1 -i -"))
            Dim p = StartFirst(candidates, input:=True, output:=False)
            If p Is Nothing Then Throw New AudioException("Nothing here can play sound: install " & If(OperatingSystem.IsMacOS(), "sox (brew install sox) or ffmpeg", "pulseaudio-utils (paplay), alsa-utils (aplay), sox or ffmpeg") & ".")
            Return New ProcessSpeaker(p)
        End Function

        Public Shared Function OpenMicrophone(rate As Integer, onAudio As Action(Of Byte(), Integer)) As IMicrophone
            Dim file = Environment.GetEnvironmentVariable("AGENTIC_AUDIO_IN")
            If Not String.IsNullOrWhiteSpace(file) Then Return New WavMicrophone(file, rate, onAudio)
            If OperatingSystem.IsWindows() Then Return New WinMicrophone(rate, onAudio)
            Dim r = rate.ToString(Globalization.CultureInfo.InvariantCulture)
            Dim candidates As New List(Of (Cmd As String, Args As String))
            If OperatingSystem.IsMacOS() Then
                candidates.Add(("sox", $"-q -d -t raw -r {r} -e signed -b 16 -c 1 -"))
                candidates.Add(("ffmpeg", $"-loglevel error -f avfoundation -i :0 -ac 1 -ar {r} -f s16le -"))
            Else
                candidates.Add(("parecord", $"--raw --format=s16le --rate={r} --channels=1"))
                candidates.Add(("arecord", $"-q -f S16_LE -r {r} -c 1 -t raw"))
                candidates.Add(("sox", $"-q -d -t raw -r {r} -e signed -b 16 -c 1 -"))
                candidates.Add(("ffmpeg", $"-loglevel error -f pulse -i default -ac 1 -ar {r} -f s16le -"))
            End If
            Dim p = StartFirst(candidates, input:=False, output:=True)
            If p Is Nothing Then Throw New AudioException("Nothing here can record the microphone: install " & If(OperatingSystem.IsMacOS(), "sox (brew install sox) or ffmpeg", "pulseaudio-utils (parecord), alsa-utils (arecord), sox or ffmpeg") & ".")
            Return New ProcessMicrophone(p, onAudio)
        End Function

        ''' <summary>The first of the commands that starts (a missing one is skipped).</summary>
        Private Shared Function StartFirst(candidates As List(Of (Cmd As String, Args As String)), input As Boolean, output As Boolean) As Process
            For Each c In candidates
                Try
                    Dim psi As New ProcessStartInfo(c.Cmd, c.Args) With {.UseShellExecute = False, .RedirectStandardInput = input, .RedirectStandardOutput = output,
                                                                        .RedirectStandardError = True, .CreateNoWindow = True}
                    Dim p = Process.Start(psi)
                    If p IsNot Nothing Then Return p
                Catch ex As Win32Exception
                    ' Not installed: the next one.
                End Try
            Next
            Return Nothing
        End Function

        ''' <summary>A WAV file of 16-bit mono PCM.</summary>
        Public Shared Function Wav(pcm As Byte(), count As Integer, rate As Integer) As Byte()
            Dim b(43 + count) As Byte
            Dim put = Sub(at As Integer, s As String) Text.Encoding.ASCII.GetBytes(s).CopyTo(b, at)
            put(0, "RIFF")
            BitConverter.GetBytes(36 + count).CopyTo(b, 4)
            put(8, "WAVEfmt ")
            BitConverter.GetBytes(16).CopyTo(b, 16)
            BitConverter.GetBytes(CShort(1)).CopyTo(b, 20)
            BitConverter.GetBytes(CShort(1)).CopyTo(b, 22)
            BitConverter.GetBytes(rate).CopyTo(b, 24)
            BitConverter.GetBytes(rate * 2).CopyTo(b, 28)
            BitConverter.GetBytes(CShort(2)).CopyTo(b, 32)
            BitConverter.GetBytes(CShort(16)).CopyTo(b, 34)
            put(36, "data")
            BitConverter.GetBytes(count).CopyTo(b, 40)
            Buffer.BlockCopy(pcm, 0, b, 44, count)
            Return b
        End Function

        ''' <summary>Loudness (RMS, 0 to 1) of 16-bit samples.</summary>
        Public Shared Function Rms(pcm As Byte(), offset As Integer, samples As Integer) As Double
            Dim sum = 0.0
            For i = 0 To samples - 1
                Dim v = BitConverter.ToInt16(pcm, offset + i * 2) / 32768.0
                sum += v * v
            Next
            Return Math.Sqrt(sum / Math.Max(1, samples))
        End Function
    End Class

    ' ------------------------------------------------------------------ Windows: waveOut / waveIn

    Friend Module WinMm
        Public Const WaveMapper As UInteger = &HFFFFFFFFUI
        Public Const WhdrDone As Integer = 1

        <StructLayout(LayoutKind.Sequential)>
        Public Structure WaveFormat
            Public Tag As UShort
            Public Channels As UShort
            Public Rate As UInteger
            Public BytesPerSec As UInteger
            Public Align As UShort
            Public Bits As UShort
            Public Extra As UShort
        End Structure

        <StructLayout(LayoutKind.Sequential)>
        Public Structure WaveHdr
            Public Data As IntPtr
            Public Length As UInteger
            Public Recorded As UInteger
            Public User As IntPtr
            Public Flags As Integer
            Public Loops As UInteger
            Public [Next] As IntPtr
            Public Reserved As IntPtr
        End Structure

        Public ReadOnly HdrSize As Integer = Marshal.SizeOf(GetType(WaveHdr))

        Public Function Format(rate As Integer) As WaveFormat
            Return New WaveFormat With {.Tag = 1, .Channels = 1, .Rate = CUInt(rate), .BytesPerSec = CUInt(rate * 2), .Align = 2, .Bits = 16}
        End Function

        ''' <summary>A header and its buffer in unmanaged memory (the driver uses them while they play or record).</summary>
        Public Function NewHeader(size As Integer, Optional data As Byte() = Nothing, Optional count As Integer = 0) As IntPtr
            Dim buf = Marshal.AllocHGlobal(size)
            If data IsNot Nothing Then Marshal.Copy(data, 0, buf, count)
            Dim hdr = Marshal.AllocHGlobal(HdrSize)
            Marshal.StructureToPtr(New WaveHdr With {.Data = buf, .Length = CUInt(If(data Is Nothing, size, count))}, hdr, False)
            Return hdr
        End Function

        Public Function Header(hdr As IntPtr) As WaveHdr
            Return Marshal.PtrToStructure(Of WaveHdr)(hdr)
        End Function

        Public Sub FreeHeader(hdr As IntPtr)
            Marshal.FreeHGlobal(Header(hdr).Data)
            Marshal.FreeHGlobal(hdr)
        End Sub

        <DllImport("winmm.dll")> Public Function waveOutOpen(ByRef h As IntPtr, dev As UIntPtr, ByRef fmt As WaveFormat, cb As IntPtr, inst As IntPtr, flags As Integer) As Integer
        End Function
        <DllImport("winmm.dll")> Public Function waveOutPrepareHeader(h As IntPtr, hdr As IntPtr, size As Integer) As Integer
        End Function
        <DllImport("winmm.dll")> Public Function waveOutUnprepareHeader(h As IntPtr, hdr As IntPtr, size As Integer) As Integer
        End Function
        <DllImport("winmm.dll")> Public Function waveOutWrite(h As IntPtr, hdr As IntPtr, size As Integer) As Integer
        End Function
        <DllImport("winmm.dll")> Public Function waveOutReset(h As IntPtr) As Integer
        End Function
        <DllImport("winmm.dll")> Public Function waveOutClose(h As IntPtr) As Integer
        End Function
        <DllImport("winmm.dll")> Public Function waveInOpen(ByRef h As IntPtr, dev As UIntPtr, ByRef fmt As WaveFormat, cb As IntPtr, inst As IntPtr, flags As Integer) As Integer
        End Function
        <DllImport("winmm.dll")> Public Function waveInPrepareHeader(h As IntPtr, hdr As IntPtr, size As Integer) As Integer
        End Function
        <DllImport("winmm.dll")> Public Function waveInUnprepareHeader(h As IntPtr, hdr As IntPtr, size As Integer) As Integer
        End Function
        <DllImport("winmm.dll")> Public Function waveInAddBuffer(h As IntPtr, hdr As IntPtr, size As Integer) As Integer
        End Function
        <DllImport("winmm.dll")> Public Function waveInStart(h As IntPtr) As Integer
        End Function
        <DllImport("winmm.dll")> Public Function waveInReset(h As IntPtr) As Integer
        End Function
        <DllImport("winmm.dll")> Public Function waveInClose(h As IntPtr) As Integer
        End Function
    End Module

    Friend NotInheritable Class WinSpeaker
        Implements ISpeaker
        Private _h As IntPtr
        Private ReadOnly _pending As New List(Of IntPtr)
        Private ReadOnly _lock As New Object()

        Public Sub New(rate As Integer)
            Dim f = WinMm.Format(rate)
            Dim err = WinMm.waveOutOpen(_h, New UIntPtr(WinMm.WaveMapper), f, IntPtr.Zero, IntPtr.Zero, 0)
            If err <> 0 Then Throw New AudioException($"No speaker could be opened (waveOutOpen error {err}): check that one is connected and that Windows Audio is running.")
        End Sub

        Public Sub Write(pcm As Byte(), count As Integer) Implements ISpeaker.Write
            If count <= 0 Then Return
            SyncLock _lock
                Reap(False)
                Dim hdr = WinMm.NewHeader(count, pcm, count)
                WinMm.waveOutPrepareHeader(_h, hdr, WinMm.HdrSize)
                WinMm.waveOutWrite(_h, hdr, WinMm.HdrSize)
                _pending.Add(hdr)
            End SyncLock
        End Sub

        ''' <summary>Frees the buffers that have played (all of them after a reset).</summary>
        Private Sub Reap(all As Boolean)
            For i = _pending.Count - 1 To 0 Step -1
                Dim hdr = _pending(i)
                If all OrElse (WinMm.Header(hdr).Flags And WinMm.WhdrDone) <> 0 Then
                    WinMm.waveOutUnprepareHeader(_h, hdr, WinMm.HdrSize)
                    WinMm.FreeHeader(hdr)
                    _pending.RemoveAt(i)
                End If
            Next
        End Sub

        Public Async Function DrainAsync(ct As CancellationToken) As Task Implements ISpeaker.DrainAsync
            While True
                SyncLock _lock
                    Reap(False)
                    If _pending.Count = 0 Then Return
                End SyncLock
                If ct.IsCancellationRequested Then
                    Halt()
                    Return
                End If
                Try
                    Await Task.Delay(30, ct).ConfigureAwait(False)
                Catch ex As OperationCanceledException
                End Try
            End While
        End Function

        Private Sub Halt()
            SyncLock _lock
                If _h = IntPtr.Zero Then Return
                WinMm.waveOutReset(_h)
                Reap(True)
            End SyncLock
        End Sub

        Public Sub Dispose() Implements IDisposable.Dispose
            Halt()
            SyncLock _lock
                If _h <> IntPtr.Zero Then WinMm.waveOutClose(_h)
                _h = IntPtr.Zero
            End SyncLock
        End Sub
    End Class

    Friend NotInheritable Class WinMicrophone
        Implements IMicrophone
        Private ReadOnly _thread As Thread
        Private _stop As Boolean
        Private _problem As String

        Public Sub New(rate As Integer, onAudio As Action(Of Byte(), Integer))
            Dim h As IntPtr
            Dim f = WinMm.Format(rate)
            Dim err = WinMm.waveInOpen(h, New UIntPtr(WinMm.WaveMapper), f, IntPtr.Zero, IntPtr.Zero, 0)
            If err <> 0 Then Throw New AudioException($"No microphone could be opened (waveInOpen error {err}): check that one is connected and that Windows Audio is running.")
            ' 8 buffers of 50 ms, read in turn as the driver fills them.
            Dim bytes = rate \ 10
            Dim hdrs(7) As IntPtr
            For i = 0 To hdrs.Length - 1
                hdrs(i) = WinMm.NewHeader(bytes)
                WinMm.waveInPrepareHeader(h, hdrs(i), WinMm.HdrSize)
                WinMm.waveInAddBuffer(h, hdrs(i), WinMm.HdrSize)
            Next
            WinMm.waveInStart(h)
            _thread = New Thread(Sub()
                                     Dim buf(bytes - 1) As Byte
                                     Dim at = 0
                                     Try
                                         While Not _stop
                                             Dim x = WinMm.Header(hdrs(at))
                                             If (x.Flags And WinMm.WhdrDone) = 0 Then
                                                 Thread.Sleep(5)
                                                 Continue While
                                             End If
                                             Dim got = CInt(x.Recorded)
                                             If got > 0 Then
                                                 Marshal.Copy(x.Data, buf, 0, got)
                                                 onAudio(buf, got)
                                             End If
                                             WinMm.waveInUnprepareHeader(h, hdrs(at), WinMm.HdrSize)
                                             x.Flags = 0
                                             x.Recorded = 0
                                             Marshal.StructureToPtr(x, hdrs(at), False)
                                             WinMm.waveInPrepareHeader(h, hdrs(at), WinMm.HdrSize)
                                             WinMm.waveInAddBuffer(h, hdrs(at), WinMm.HdrSize)
                                             at = (at + 1) Mod hdrs.Length
                                         End While
                                     Catch ex As Exception
                                         _problem = "Recording stopped: " & ex.Message
                                     Finally
                                         WinMm.waveInReset(h)
                                         For Each hdr In hdrs
                                             WinMm.waveInUnprepareHeader(h, hdr, WinMm.HdrSize)
                                             WinMm.FreeHeader(hdr)
                                         Next
                                         WinMm.waveInClose(h)
                                     End Try
                                 End Sub) With {.IsBackground = True, .Name = "microphone"}
            _thread.Start()
        End Sub

        Public ReadOnly Property Problem As String Implements IMicrophone.Problem
            Get
                Return _problem
            End Get
        End Property

        Public Sub Dispose() Implements IDisposable.Dispose
            _stop = True
            _thread.Join(2000)
        End Sub
    End Class

    ' ------------------------------------------------------------------ Linux and macOS: a player or recorder process

    Friend NotInheritable Class ProcessSpeaker
        Implements ISpeaker
        Private ReadOnly _p As Process
        Private _broken As Boolean

        Public Sub New(p As Process)
            _p = p
            ' Its complaints would mix with the terminal's output: they are read and dropped.
            AddHandler _p.ErrorDataReceived, AddressOf Ignore
            _p.BeginErrorReadLine()
        End Sub

        Private Shared Sub Ignore(sender As Object, e As DataReceivedEventArgs)
        End Sub

        Public Sub Write(pcm As Byte(), count As Integer) Implements ISpeaker.Write
            If _broken OrElse count <= 0 Then Return
            Try
                _p.StandardInput.BaseStream.Write(pcm, 0, count)
                _p.StandardInput.BaseStream.Flush()
            Catch ex As IOException
                _broken = True
            End Try
        End Sub

        Public Async Function DrainAsync(ct As CancellationToken) As Task Implements ISpeaker.DrainAsync
            Try
                _p.StandardInput.Close()
            Catch ex As IOException
            End Try
            Try
                Await _p.WaitForExitAsync(ct).ConfigureAwait(False)
            Catch ex As OperationCanceledException
                Kill()
            End Try
        End Function

        Private Sub Kill()
            Try
                If Not _p.HasExited Then _p.Kill(True)
            Catch ex As Exception When TypeOf ex Is InvalidOperationException OrElse TypeOf ex Is Win32Exception
            End Try
        End Sub

        Public Sub Dispose() Implements IDisposable.Dispose
            Kill()
            _p.Dispose()
        End Sub
    End Class

    Friend NotInheritable Class ProcessMicrophone
        Implements IMicrophone
        Private ReadOnly _p As Process
        Private ReadOnly _thread As Thread
        Private _problem As String
        Private _closing As Boolean
        Private ReadOnly _stderr As New Text.StringBuilder()

        Public Sub New(p As Process, onAudio As Action(Of Byte(), Integer))
            _p = p
            AddHandler _p.ErrorDataReceived, Sub(s, e)
                                                 If e.Data IsNot Nothing Then
                                                     SyncLock _stderr
                                                         _stderr.AppendLine(e.Data)
                                                     End SyncLock
                                                 End If
                                             End Sub
            _p.BeginErrorReadLine()
            _thread = New Thread(Sub()
                                     Dim buf(3199) As Byte
                                     Try
                                         Dim s = _p.StandardOutput.BaseStream
                                         While True
                                             Dim n = s.Read(buf, 0, buf.Length)
                                             If n <= 0 Then Exit While
                                             onAudio(buf, n)
                                         End While
                                     Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is ObjectDisposedException OrElse TypeOf ex Is InvalidOperationException
                                     End Try
                                     If Not _closing Then
                                         Dim said As String
                                         SyncLock _stderr
                                             said = _stderr.ToString().Trim()
                                         End SyncLock
                                         _problem = If(said.Length > 0, said, $"The recorder ({_p.StartInfo.FileName}) stopped.")
                                     End If
                                 End Sub) With {.IsBackground = True, .Name = "microphone"}
            _thread.Start()
        End Sub

        Public ReadOnly Property Problem As String Implements IMicrophone.Problem
            Get
                Return _problem
            End Get
        End Property

        Public Sub Dispose() Implements IDisposable.Dispose
            _closing = True
            Try
                If Not _p.HasExited Then _p.Kill(True)
            Catch ex As Exception When TypeOf ex Is InvalidOperationException OrElse TypeOf ex Is Win32Exception
            End Try
            _thread.Join(2000)
            _p.Dispose()
        End Sub
    End Class

    ' ------------------------------------------------------------------ tests and machines without sound

    ''' <summary>AGENTIC_AUDIO_OUT: the speech is appended to a file (raw PCM).</summary>
    Friend NotInheritable Class FileSpeaker
        Implements ISpeaker
        Private ReadOnly _file As FileStream

        Public Sub New(path As String)
            _file = New FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)
        End Sub

        Public Sub Write(pcm As Byte(), count As Integer) Implements ISpeaker.Write
            _file.Write(pcm, 0, count)
            _file.Flush()
        End Sub

        Public Function DrainAsync(ct As CancellationToken) As Task Implements ISpeaker.DrainAsync
            Return Task.CompletedTask
        End Function

        Public Sub Dispose() Implements IDisposable.Dispose
            _file.Dispose()
        End Sub
    End Class

    ''' <summary>AGENTIC_AUDIO_IN: a WAV file is heard as the microphone, in real time, then silence.</summary>
    Friend NotInheritable Class WavMicrophone
        Implements IMicrophone
        Private _stop As Boolean
        Private ReadOnly _thread As Thread

        Public Sub New(path As String, rate As Integer, onAudio As Action(Of Byte(), Integer))
            Dim data = File.ReadAllBytes(path)
            Dim pcm = If(data.Length > 44 AndAlso Text.Encoding.ASCII.GetString(data, 0, 4) = "RIFF", data.Skip(44).ToArray(), data)
            Dim chunk = rate \ 10   ' 50 ms
            _thread = New Thread(Sub()
                                     Dim at = 0
                                     Dim quiet(chunk - 1) As Byte
                                     While Not _stop
                                         If at < pcm.Length Then
                                             Dim n = Math.Min(chunk, pcm.Length - at)
                                             Dim part(n - 1) As Byte
                                             Buffer.BlockCopy(pcm, at, part, 0, n)
                                             onAudio(part, n)
                                             at += n
                                         Else
                                             onAudio(quiet, quiet.Length)
                                         End If
                                         Thread.Sleep(50)
                                     End While
                                 End Sub) With {.IsBackground = True, .Name = "microphone"}
            _thread.Start()
        End Sub

        Public ReadOnly Property Problem As String Implements IMicrophone.Problem
            Get
                Return Nothing
            End Get
        End Property

        Public Sub Dispose() Implements IDisposable.Dispose
            _stop = True
            _thread.Join(2000)
        End Sub
    End Class

End Namespace
