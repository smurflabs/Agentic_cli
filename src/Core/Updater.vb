Option Strict On
Option Explicit On
Option Infer On

Imports System.IO
Imports System.Security.Cryptography
Imports System.Threading
Imports AgenticCli.Api

Namespace Core

    ''' <summary>What the server says about this install (GET /api/updates/cli).</summary>
    Friend NotInheritable Class UpdateInfo
        Public Property Available As Boolean
        Public Property Mandatory As Boolean
        Public Property Version As String
        Public Property Sha256 As String
        Public Property Size As Long
        Public Property DownloadUrl As String
        Public Property Notes As String
        Public Property Reason As String
    End Class

    ''' <summary>
    ''' Keeps agentic.exe (agentic on Linux) up to date from the Agentic server. Each platform has its own channel: "cli" for
    ''' Windows, "cli-linux-x64" for Linux. When the server's version is newer, or the same version with another SHA-256 (a
    ''' rebuild), the new binary is downloaded next to this one, checked, and swapped in: a running binary can be renamed
    ''' (on Linux too), so agentic.exe becomes agentic.exe.old-… and the new file takes its name (keeping its file mode).
    ''' The next start uses it and deletes the old file. Only published single-file builds update; a development build
    ''' (bin\Debug, with agentic.dll next to it) never does.
    ''' Off with "autoUpdates": false in settings, or DISABLE_AUTOUPDATER=1 / AGENTIC_DISABLE_AUTOUPDATE=1.
    ''' </summary>
    Friend NotInheritable Class Updater

        ''' <summary>This build's update channel on the server ("cli", "cli-linux-x64").</summary>
        Public Shared ReadOnly App As String = Platform.UpdateApp("cli")
        Private Shared _selfSha As String

        Private Sub New()
        End Sub

        Public Shared ReadOnly Property ExePath As String
            Get
                Return Environment.ProcessPath
            End Get
        End Property

        ''' <summary>True for a published single-file agentic.exe / agentic (the only kind that can replace itself).</summary>
        Public Shared Function IsUpdatableBuild() As Boolean
            Return String.IsNullOrEmpty(GetType(Updater).Assembly.Location) AndAlso ExePath IsNot Nothing AndAlso
                   (Not OperatingSystem.IsWindows() OrElse ExePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        End Function

        ''' <summary>Why automatic updates are off, or Nothing when they are on.</summary>
        Public Shared Function DisabledReason(settings As Settings) As String
            If Flag("DISABLE_AUTOUPDATER") OrElse Flag("AGENTIC_DISABLE_AUTOUPDATE") Then Return "turned off by DISABLE_AUTOUPDATER"
            If settings IsNot Nothing AndAlso Not settings.Bool("autoUpdates", True) Then Return "turned off in settings (autoUpdates)"
            If Not IsUpdatableBuild() Then Return "development build"
            Return Nothing
        End Function

        Private Shared Function Flag(name As String) As Boolean
            Dim v = Environment.GetEnvironmentVariable(name)
            Return Not String.IsNullOrWhiteSpace(v) AndAlso v.Trim() <> "0" AndAlso Not v.Trim().Equals("false", StringComparison.OrdinalIgnoreCase)
        End Function

        ''' <summary>SHA-256 of the running exe (computed once).</summary>
        Public Shared Function SelfSha256() As String
            If _selfSha Is Nothing AndAlso ExePath IsNot Nothing Then
                Try
                    Using s As New FileStream(ExePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite Or FileShare.Delete, 1 << 16)
                        _selfSha = Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant()
                    End Using
                Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
                    _selfSha = ""
                End Try
            End If
            Return If(_selfSha, "")
        End Function

        ''' <summary>This computer's install id (lets the server's admins see which version each install runs).</summary>
        Public Shared Function InstallId() As String
            Dim file = Path.Combine(Paths.AgenticHome(), "install-id")
            Try
                If IO.File.Exists(file) Then
                    Dim id = IO.File.ReadAllText(file).Trim()
                    If id.Length >= 8 AndAlso id.Length <= 64 Then Return id
                End If
                Dim fresh = "cli-" & Guid.NewGuid().ToString("N").Substring(0, 16)
                IO.File.WriteAllText(file, fresh)
                Return fresh
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
                Return Nothing
            End Try
        End Function

        ''' <summary>Asks the server. Nothing when it publishes no CLI build (or is too old to know about updates).</summary>
        Public Shared Async Function CheckAsync(api As ApiClient, ct As CancellationToken) As Task(Of UpdateInfo)
            Dim q = $"/api/updates/{App}?version={Uri.EscapeDataString(AppInfo.Version)}&sha256={SelfSha256()}" &
                    $"&installId={Uri.EscapeDataString(If(InstallId(), ""))}&machine={Uri.EscapeDataString(Environment.MachineName)}"
            Dim r As Text.Json.JsonElement
            Try
                r = Await api.GetAsync(q, ct, 20).ConfigureAwait(False)
            Catch ex As ApiException When ex.IsNotFound OrElse ex.Status = 409
                Return Nothing
            End Try
            Dim latest = Jx.Prop(r, "latest")
            Return New UpdateInfo With {
                .Available = Jx.BoolOf(r, "updateAvailable"), .Mandatory = Jx.BoolOf(r, "mandatory"), .Reason = Jx.StrOf(r, "reason"),
                .Version = Jx.StrOf(latest, "version"), .Sha256 = Jx.StrOf(latest, "sha256").ToLowerInvariant(), .Size = Jx.LongOf(latest, "size"),
                .DownloadUrl = Jx.StrOf(latest, "downloadUrl", $"/api/updates/{App}/download"), .Notes = Jx.StrOf(latest, "notes", Nothing)}
        End Function

        ''' <summary>Downloads the new exe, checks its size and SHA-256 and puts it in place of this one (used from the next start).</summary>
        Public Shared Async Function InstallAsync(api As ApiClient, info As UpdateInfo, ct As CancellationToken, Optional progress As Action(Of Long, Long) = Nothing) As Task
            Dim exe = ExePath
            Dim temp = exe & ".new-" & Environment.ProcessId.ToString(Globalization.CultureInfo.InvariantCulture)
            Dim lockFile = exe & ".update.lock"
            ' Two sessions starting at once: only one installs.
            Using lck = TryLock(lockFile)
                If lck Is Nothing Then Throw New IOException("Another agentic is installing the update.")
                Try
                    Dim got = Await api.DownloadAsync(info.DownloadUrl, temp, ct, progress).ConfigureAwait(False)
                    If (info.Size > 0 AndAlso got.Size <> info.Size) OrElse Not got.Sha256.Equals(info.Sha256, StringComparison.OrdinalIgnoreCase) Then
                        Throw New InvalidDataException("The download did not match the server's SHA-256; nothing was changed.")
                    End If
                    Platform.CopyFileMode(exe, temp)
                    Dim old = exe & ".old-" & DateTime.UtcNow.Ticks.ToString(Globalization.CultureInfo.InvariantCulture)
                    File.Move(exe, old)
                    Try
                        File.Move(temp, exe)
                    Catch
                        File.Move(old, exe)
                        Throw
                    End Try
                    _selfSha = got.Sha256
                Finally
                    TryDelete(temp)
                End Try
            End Using
            TryDelete(lockFile)
        End Function

        Private Shared Function TryLock(file As String) As FileStream
            Try
                Return New FileStream(file, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose)
            Catch ex As IOException
                Return Nothing
            End Try
        End Function

        ''' <summary>At start: removes what earlier updates left (old exes no longer running, unfinished downloads).</summary>
        Public Shared Sub CleanUp()
            If Not IsUpdatableBuild() Then Return
            Try
                Dim dir = Path.GetDirectoryName(ExePath)
                Dim name = Path.GetFileName(ExePath)
                For Each f In Directory.GetFiles(dir, name & ".old-*").Concat(Directory.GetFiles(dir, name & ".new-*"))
                    TryDelete(f)
                Next
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
            End Try
        End Sub

        Private Shared Sub TryDelete(f As String)
            Try
                If File.Exists(f) Then File.Delete(f)
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
                ' Still running (another session) or locked: the next start tries again.
            End Try
        End Sub

    End Class

End Namespace
