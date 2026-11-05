Option Strict On
Option Explicit On
Option Infer On

Imports System.IO
Imports System.Text
Imports System.Text.Json
Imports System.Text.Json.Nodes
Imports System.Text.RegularExpressions
Imports System.Threading
Imports AgenticCli.Agent
Imports AgenticCli.Core
Imports AgenticCli.Ui

Namespace Tools

    Friend NotInheritable Class BackgroundShell
        Public Property Id As String
        Public Property CommandText As String
        Public Property StartedUtc As DateTime
        Public Property Proc As Process
        Public ReadOnly Property Output As New StringBuilder()
        Public Property ReadPosition As Integer
        Public Property ExitCode As Integer?
        Public Property Killed As Boolean
        ''' <summary>When its shell ended, and when it last printed something.</summary>
        Public Property EndedUtc As DateTime?
        Public Property LastOutputUtc As DateTime?
        ''' <summary>Its output streams still open: something it started can hold them after its shell ended.</summary>
        Public OpenStreams As Integer = 2
        ''' <summary>Windows: its own job, so KillShell stops what outlived its shell too.</summary>
        Public Property Group As IntPtr

        Public ReadOnly Property Running As Boolean
            Get
                Return Not ExitCode.HasValue AndAlso Not Killed
            End Get
        End Property

        ''' <summary>Its output ended: nothing it started still writes to it.</summary>
        Public ReadOnly Property Closed As Boolean
            Get
                Return Volatile.Read(OpenStreams) <= 0
            End Get
        End Property
    End Class

    ''' <summary>
    ''' Runs commands for the Bash tool in the chosen shell (PowerShell by default on Windows; pwsh, cmd or Git Bash by setting).
    ''' Each command runs in the session's working directory; a cd inside it carries over to the next command.
    ''' </summary>
    Friend NotInheritable Class ShellManager

        Private Const CwdMarker As String = "__AGENTIC_CWD__="
        Private ReadOnly _shells As New Dictionary(Of String, BackgroundShell)(StringComparer.OrdinalIgnoreCase)
        Private _next As Integer
        Public Property Kind As String
        Public Property Env As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
        Private _exe As String

        Public Sub New(kind As String)
            Me.Kind = Resolve(kind)
        End Sub

        ''' <summary>powershell | pwsh | cmd | bash; "auto" picks pwsh when installed, else Windows PowerShell (or bash off Windows).</summary>
        Private Function Resolve(requested As String) As String
            Dim k = If(requested, "auto").Trim().ToLowerInvariant()
            If Not OperatingSystem.IsWindows() Then
                _exe = If(ProjectInfo.Which("bash"), "/bin/sh")
                Return "bash"
            End If
            Select Case k
                Case "cmd"
                    _exe = Path.Combine(Environment.SystemDirectory, "cmd.exe")
                    Return "cmd"
                Case "bash", "gitbash", "git-bash", "sh"
                    Dim git = {Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "bash.exe"),
                               Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Git", "bin", "bash.exe")}.FirstOrDefault(Function(f) File.Exists(f))
                    If git IsNot Nothing Then
                        _exe = git
                        Return "bash"
                    End If
                Case "pwsh"
                    Dim p = ProjectInfo.Which("pwsh")
                    If p IsNot Nothing Then
                        _exe = p
                        Return "pwsh"
                    End If
                Case "auto", ""
                    Dim p = ProjectInfo.Which("pwsh")
                    If p IsNot Nothing Then
                        _exe = p
                        Return "pwsh"
                    End If
            End Select
            _exe = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe")
            If Not File.Exists(_exe) Then _exe = "powershell.exe"
            Return "powershell"
        End Function

        ''' <summary>The shell's name for the model's instructions.</summary>
        Public Function Describe() As String
            Select Case Kind
                Case "pwsh" : Return "PowerShell 7 (pwsh) — use PowerShell syntax"
                Case "powershell" : Return "Windows PowerShell 5.1 — use PowerShell syntax; && and || are not available, use ; or if ($?) { }"
                Case "cmd" : Return "cmd.exe — use cmd syntax"
                Case Else : Return "bash (Git Bash on Windows) — use POSIX shell syntax with forward slashes"
            End Select
        End Function

        Private Function BuildScript(commandText As String, cwd As String) As (Exe As String, Args As String(), ScriptFile As String)
            Dim tmp = Path.Combine(Path.GetTempPath(), "agentic-" & Guid.NewGuid().ToString("N").Substring(0, 12))
            Select Case Kind
                Case "cmd"
                    Dim file = tmp & ".cmd"
                    IO.File.WriteAllText(file, "@echo off" & vbCrLf & "chcp 65001 >nul" & vbCrLf & $"cd /d ""{cwd}""" & vbCrLf & commandText & vbCrLf &
                                              "set __AGENTIC_EC=%ERRORLEVEL%" & vbCrLf & "echo." & vbCrLf & "echo " & CwdMarker & "%CD%" & vbCrLf & "exit /b %__AGENTIC_EC%" & vbCrLf, New UTF8Encoding(False))
                    Return (_exe, {"/d", "/c", file}, file)
                Case "bash"
                    Dim file = tmp & ".sh"
                    Dim posix = If(OperatingSystem.IsWindows(), "/" & cwd.Substring(0, 1).ToLowerInvariant() & cwd.Substring(2).Replace("\"c, "/"c), cwd)
                    IO.File.WriteAllText(file, $"cd '{posix.Replace("'", "'\''")}' || exit 1" & vbLf & commandText.Replace(vbCrLf, vbLf) & vbLf &
                                              "__agentic_ec=$?" & vbLf & "printf '\n" & CwdMarker & "%s\n' ""$(pwd -W 2>/dev/null || pwd)""" & vbLf & "exit $__agentic_ec" & vbLf, New UTF8Encoding(False))
                    Return (_exe, {"--noprofile", "--norc", file}, file)
                Case Else
                    Dim file = tmp & ".ps1"
                    Dim sb As New StringBuilder()
                    sb.AppendLine("$ErrorActionPreference = 'Continue'")
                    sb.AppendLine("$ProgressPreference = 'SilentlyContinue'")
                    sb.AppendLine("try { [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false) } catch { }")
                    sb.AppendLine("$OutputEncoding = [System.Text.UTF8Encoding]::new($false)")
                    sb.AppendLine("$global:LASTEXITCODE = $null")
                    sb.AppendLine("Set-Location -LiteralPath '" & cwd.Replace("'", "''") & "'")
                    sb.AppendLine(commandText)
                    sb.AppendLine("$__agentic_ok = $?")
                    sb.AppendLine("$__agentic_ec = if ($null -ne $global:LASTEXITCODE) { $global:LASTEXITCODE } elseif ($__agentic_ok) { 0 } else { 1 }")
                    sb.AppendLine("[Console]::Out.WriteLine(''); [Console]::Out.WriteLine('" & CwdMarker & "' + (Get-Location).ProviderPath)")
                    sb.AppendLine("exit $__agentic_ec")
                    ' A BOM makes Windows PowerShell read the script as UTF-8.
                    IO.File.WriteAllText(file, sb.ToString(), New UTF8Encoding(True))
                    Return (_exe, {"-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", file}, file)
            End Select
        End Function

        Private Function StartProcess(commandText As String, cwd As String, sink As Action(Of String), Optional extraEnv As IDictionary(Of String, String) = Nothing,
                                      Optional streamEnded As Action = Nothing) As (Proc As Process, ScriptFile As String)
            Dim s = BuildScript(commandText, cwd)
            Dim psi As New ProcessStartInfo(s.Exe) With {
                .UseShellExecute = False, .CreateNoWindow = True, .RedirectStandardOutput = True, .RedirectStandardError = True, .RedirectStandardInput = True,
                .StandardOutputEncoding = New UTF8Encoding(False), .StandardErrorEncoding = New UTF8Encoding(False), .WorkingDirectory = cwd}
            For Each a In s.Args
                psi.ArgumentList.Add(a)
            Next
            For Each kv In Env
                psi.Environment(kv.Key) = kv.Value
            Next
            psi.Environment("AGENTIC") = "1"
            psi.Environment("NO_COLOR") = "1"
            psi.Environment("TERM") = "dumb"
            psi.Environment("GIT_PAGER") = "cat"
            psi.Environment("PAGER") = "cat"
            psi.Environment("GIT_TERMINAL_PROMPT") = "0"
            If extraEnv IsNot Nothing Then
                For Each kv In extraEnv
                    psi.Environment(kv.Key) = kv.Value
                Next
            End If
            Dim p = Process.Start(psi)
            p.StandardInput.Close()
            Dim pump = Sub(reader As StreamReader)
                           Task.Run(Async Function()
                                        Dim buf(4095) As Char
                                        Try
                                            While True
                                                Dim n = Await reader.ReadAsync(buf, 0, buf.Length).ConfigureAwait(False)
                                                If n <= 0 Then Exit While
                                                sink(New String(buf, 0, n))
                                            End While
                                        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is ObjectDisposedException OrElse TypeOf ex Is InvalidOperationException
                                        End Try
                                        streamEnded?.Invoke()
                                    End Function)
                       End Sub
            pump(p.StandardOutput)
            pump(p.StandardError)
            Return (p, s.ScriptFile)
        End Function

        Private Shared Sub KillTree(p As Process)
            Try
                If Not p.HasExited Then p.Kill(True)
            Catch ex As Exception When TypeOf ex Is InvalidOperationException OrElse TypeOf ex Is ComponentModel.Win32Exception OrElse TypeOf ex Is NotSupportedException
            End Try
        End Sub

        Private Shared Sub Cleanup(file As String)
            Try
                If IO.File.Exists(file) Then IO.File.Delete(file)
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
            End Try
        End Sub

        ''' <summary>
        ''' Runs a command for the Bash tool and returns its output and the new directory. It is never stopped for taking long:
        ''' after waitMs it goes on as a background shell (BackgroundId) and the model gets control back. It returns as soon as
        ''' the shell ends, even when something the command started keeps its output open (that goes on as a background shell
        ''' too). Only the user interrupting stops it.
        ''' </summary>
        Public Async Function RunAsync(commandText As String, cwd As String, waitMs As Integer, ct As CancellationToken, Optional stopAtWait As Boolean = False) As Task(Of (ExitCode As Integer, Output As String, NewCwd As String, BackgroundId As String, Interrupted As Boolean, StillRunning As Boolean))
            Dim sh = StartShell(commandText, cwd, Nothing, track:=False)
            Dim deadline = DateTime.UtcNow.AddMilliseconds(waitMs)
            Dim interrupted = False, stillRunning = False
            Do
                If ShellEnded(sh) Then
                    ' Its output: until it ends, or 2 s more (something it started may keep it open).
                    Dim grace = DateTime.UtcNow.AddSeconds(2)
                    While Not sh.Closed AndAlso DateTime.UtcNow < grace AndAlso Not ct.IsCancellationRequested
                        Await Task.Delay(40).ConfigureAwait(False)
                    End While
                    Exit Do
                End If
                If ct.IsCancellationRequested Then
                    interrupted = True
                    Exit Do
                End If
                If DateTime.UtcNow >= deadline Then
                    stillRunning = True
                    Exit Do
                End If
                Try
                    Await Task.Delay(40, ct).ConfigureAwait(False)
                Catch ex As OperationCanceledException
                End Try
            Loop
            ' The CLI's own short queries (git, a command template's !lines) keep a hard limit: they are stopped, not handed over.
            Dim stopped = interrupted OrElse (stillRunning AndAlso stopAtWait)
            If stopped Then
                sh.Killed = True
                StopShell(sh)
                stillRunning = False
            End If
            Dim text = TakeOutput(sh)
            Dim newCwd As String = Nothing
            Dim m = Regex.Match(text, "\r?\n?" & CwdMarker & "(.*?)\r?\n?$", RegexOptions.RightToLeft)
            If m.Success Then
                newCwd = m.Groups(1).Value.Trim()
                text = text.Remove(m.Index, m.Length)
            End If
            Dim background As String = Nothing
            If Not stopped AndAlso (stillRunning OrElse Not sh.Closed) Then
                ' Still running, or something it started still prints: BashOutput reads it, KillShell stops it.
                Register(sh)
                background = sh.Id
            Else
                CommandGroups.Release(sh.Group)
                sh.Group = IntPtr.Zero
            End If
            Dim code = If(stopped OrElse Not sh.ExitCode.HasValue, -1, sh.ExitCode.Value)
            Return (code, StripControl(text).TrimEnd(), newCwd, background, interrupted, stillRunning)
        End Function

        ''' <summary>Starts a command whose output is collected into its shell record (registered for BashOutput when register).</summary>
        Private Function StartShell(commandText As String, cwd As String, extraEnv As IDictionary(Of String, String), track As Boolean) As BackgroundShell
            Dim sh As New BackgroundShell With {.CommandText = commandText, .StartedUtc = DateTime.UtcNow}
            Dim started = StartProcess(commandText, cwd, Sub(t)
                                                              SyncLock sh.Output
                                                                  sh.Output.Append(t)
                                                                  sh.LastOutputUtc = DateTime.UtcNow
                                                                  If sh.Output.Length > 2_000_000 Then
                                                                      Dim cut = sh.Output.Length - 1_000_000
                                                                      sh.Output.Remove(0, cut)
                                                                      sh.ReadPosition = Math.Max(0, sh.ReadPosition - cut)
                                                                  End If
                                                              End SyncLock
                                                          End Sub, extraEnv, Sub() Interlocked.Decrement(sh.OpenStreams))
            sh.Proc = started.Proc
            sh.Group = CommandGroups.ForCommand(sh.Proc)
            sh.Proc.EnableRaisingEvents = True
            Dim file = started.ScriptFile
            AddHandler sh.Proc.Exited, Sub(s, e)
                                           ShellEnded(sh)
                                           Cleanup(file)
                                       End Sub
            If track Then Register(sh)
            Return sh
        End Function

        Private Sub Register(sh As BackgroundShell)
            SyncLock _shells
                _next += 1
                sh.Id = "bash_" & _next
                _shells(sh.Id) = sh
            End SyncLock
        End Sub

        ''' <summary>Whether its shell has ended (its exit code and time are noted the first time).</summary>
        Friend Shared Function ShellEnded(sh As BackgroundShell) As Boolean
            If sh.EndedUtc.HasValue Then Return True
            Try
                If Not sh.Proc.HasExited Then Return False
            Catch ex As InvalidOperationException
            End Try
            sh.ExitCode = SafeExitCode(sh.Proc)
            sh.EndedUtc = DateTime.UtcNow
            Return True
        End Function

        ''' <summary>Its output not read yet (it counts as read now).</summary>
        Friend Shared Function TakeOutput(sh As BackgroundShell) As String
            SyncLock sh.Output
                Dim chunk = sh.Output.ToString(sh.ReadPosition, sh.Output.Length - sh.ReadPosition)
                sh.ReadPosition = sh.Output.Length
                Return chunk
            End SyncLock
        End Function

        ''' <summary>Stops a command and everything it started (its own job on Windows reaches what outlived its shell).</summary>
        Private Shared Sub StopShell(sh As BackgroundShell)
            If sh.Group <> IntPtr.Zero Then
                CommandGroups.Terminate(sh.Group)
                sh.Group = IntPtr.Zero
            Else
                KillTree(sh.Proc)
            End If
        End Sub

        Private Shared Function SafeExitCode(p As Process) As Integer
            Try
                Return p.ExitCode
            Catch ex As InvalidOperationException
                Return -1
            End Try
        End Function

        ''' <summary>ANSI colour codes and carriage-return progress bars do not belong in the model's input.</summary>
        Friend Shared Function StripControl(text As String) As String
            Dim t = Ansi.Strip(text)
            ' Keep only what is left after the last \r on each line (progress bars).
            Dim lines = t.Replace(vbCrLf, vbLf).Split(ChrW(10))
            For i = 0 To lines.Length - 1
                Dim cr = lines(i).LastIndexOf(ChrW(13))
                If cr >= 0 Then lines(i) = lines(i).Substring(cr + 1)
            Next
            Return String.Join(vbLf, lines)
        End Function

        Public Function StartBackground(commandText As String, cwd As String, Optional extraEnv As IDictionary(Of String, String) = Nothing) As BackgroundShell
            Return StartShell(commandText, cwd, extraEnv, track:=True)
        End Function

        Public Function [Get](id As String) As BackgroundShell
            SyncLock _shells
                Dim sh As BackgroundShell = Nothing
                Return If(_shells.TryGetValue(If(id, ""), sh), sh, Nothing)
            End SyncLock
        End Function

        Public Function All() As List(Of BackgroundShell)
            SyncLock _shells
                Return _shells.Values.ToList()
            End SyncLock
        End Function

        ''' <summary>Still doing something: its shell runs, or something it started still holds its output.</summary>
        Private Shared Function Busy(sh As BackgroundShell) As Boolean
            Return Not sh.Killed AndAlso (sh.Running OrElse Not sh.Closed)
        End Function

        Public Function Kill(id As String) As Boolean
            Dim sh = [Get](id)
            If sh Is Nothing OrElse Not Busy(sh) Then Return False
            sh.Killed = True
            StopShell(sh)
            Return True
        End Function

        Public Sub KillAll()
            For Each sh In All().Where(Function(s) Busy(s))
                Kill(sh.Id)
            Next
        End Sub

    End Class

    Friend NotInheritable Class BashTool
        Implements ITool

        Public ReadOnly Property Name As String = "Bash" Implements ITool.Name
        Public ReadOnly Property IsReadOnly As Boolean = False Implements ITool.IsReadOnly
        Private ReadOnly _shell As ShellManager

        Public Sub New(shell As ShellManager)
            _shell = shell
        End Sub

        Public ReadOnly Property Description As String Implements ITool.Description
            Get
                Return "Runs a command in a shell on the user's computer: " & _shell.Describe() & ". " &
                    "It starts in the working directory; a cd carries over to later commands. Quote paths with spaces. It waits up to timeout ms (default 120000, " &
                    "max 600000) for the command; one still running then is not stopped: it goes on in the background and you get its id and output so far. " &
                    "Output is cut beyond 30000 characters. Use run_in_background for servers, watchers and long jobs, then audit them with BashOutput (status, run time, " &
                    "time since the last output, warnings when stuck or waiting for input; wait_seconds to wait for them) and stop them with KillShell. " &
                    "Do not use it to find, read or edit files (use Glob, Grep, Read, Edit, Write) or for interactive programs (no input can be given). " &
                    "Give a short description (5-10 words) of what the command does. Never commit, push or delete things unless the user asked for it."
            End Get
        End Property

        Public Function Parameters() As JsonObject Implements ITool.Parameters
            Return ToolKit.Schema({"command"}, ("command", "string", "The command to run"), ("description", "string", "What it does, in 5-10 words"),
                                  ("timeout", "integer", "Milliseconds to wait for it before it goes on in the background - it is not stopped (max 600000)"),
                                  ("run_in_background", "boolean", "Start it and return at once; audit it with BashOutput"))
        End Function

        Public Function Title(input As JsonElement, state As AgentState) As String Implements ITool.Title
            Return "Bash(" & ToolKit.OneLine(ToolKit.Arg(input, "command", ""), 100) & ")" & If(ToolKit.ArgBool(input, "run_in_background", False), " &", "")
        End Function

        Public Function Preview(input As JsonElement, state As AgentState) As String Implements ITool.Preview
            Dim cmdText = ToolKit.Arg(input, "command", "")
            Dim sb As New StringBuilder()
            For Each l In cmdText.Replace(ChrW(13), "").Split(ChrW(10)).Take(30)
                sb.Append("      ").Append(Highlighter.Line(l, If(_shell.Kind = "bash", "sh", If(_shell.Kind = "cmd", "cmd", "ps")))).Append(Environment.NewLine)
            Next
            Dim d = ToolKit.Arg(input, "description")
            If d IsNot Nothing Then sb.Append("      ").Append(Ansi.Muted(d)).Append(Environment.NewLine)
            Return sb.ToString()
        End Function

        Public Async Function RunAsync(input As JsonElement, ctx As ToolContext) As Task(Of ToolResult) Implements ITool.RunAsync
            Dim cmdText = ToolKit.Arg(input, "command")
            If String.IsNullOrWhiteSpace(cmdText) Then Return ToolResult.Fail("command is required.")
            Dim state = ctx.State
            Dim cwd = If(Directory.Exists(state.Cwd), state.Cwd, state.ProjectDir)
            If ToolKit.ArgBool(input, "run_in_background", False) Then
                Dim sh = _shell.StartBackground(cmdText, cwd)
                Return ToolResult.Done($"Command running in background with ID: {sh.Id}. Audit it with BashOutput (bash_id ""{sh.Id}""; wait_seconds to wait for it); stop it with KillShell.",
                                       $"Running in the background ({sh.Id})")
            End If
            Dim wait = Math.Max(1000, Math.Min(600000, ToolKit.ArgInt(input, "timeout", 120000)))
            Dim r = Await _shell.RunAsync(cmdText, cwd, wait, ctx.Cancel).ConfigureAwait(False)
            Dim text = r.Output
            If r.StillRunning Then
                ' Still running after its wait: not stopped - the model gets control back and decides.
                Dim handed = $"Still running after {wait \ 1000} s, so it goes on in the background with ID {r.BackgroundId} (it was not stopped)." & vbLf &
                             "Output so far:" & vbLf & ToolKit.Clip(If(text.Length = 0, "(none yet)", text)) & vbLf &
                             $"Audit it with BashOutput (bash_id ""{r.BackgroundId}""; wait_seconds to wait for it to finish); stop it with KillShell."
                Return New ToolResult With {.Ok = True, .Content = handed, .Summary = Ansi.Muted($"Still running · in the background as {r.BackgroundId}"),
                                            .Detail = ToolKit.PreviewLines(text, If(state.Verbose, 200, 4), "      ")}
            End If
            Dim notes As New List(Of String)
            If r.Interrupted Then notes.Add("The user interrupted the command.")
            If r.BackgroundId IsNot Nothing Then notes.Add($"Something this command started is still running and printing: it goes on in the background with ID {r.BackgroundId} " &
                                                           "(BashOutput shows what it prints; KillShell stops it).")
            If Not String.IsNullOrEmpty(r.NewCwd) AndAlso Directory.Exists(r.NewCwd) AndAlso Not r.NewCwd.Equals(cwd, StringComparison.OrdinalIgnoreCase) Then
                If state.InWorkingDirs(r.NewCwd) Then
                    Dim before = state.Cwd
                    state.Cwd = Path.GetFullPath(r.NewCwd)
                    state.Hooks?.Run("CwdChanged", "", New Dictionary(Of String, Object) From {{"old_cwd", before}, {"new_cwd", state.Cwd}})
                Else
                    notes.Add("Shell cwd was reset to " & cwd)
                End If
            End If
            If r.ExitCode > 0 Then notes.Add("Exit code " & r.ExitCode)
            Dim content = ToolKit.Clip(If(text.Length = 0, "(no output)", text)) & If(notes.Count > 0, vbLf & String.Join(vbLf, notes), "")
            Dim ok = r.ExitCode = 0 AndAlso Not r.Interrupted
            Dim summary = If(text.Length = 0, If(ok, Ansi.Muted("(No content)"), ""), Nothing)
            If Not ok Then summary = Ansi.Bad(If(r.Interrupted, "Interrupted", "Exit code " & r.ExitCode)) & If(summary, "")
            Return New ToolResult With {.Ok = ok, .Content = content, .Summary = summary,
                                        .Detail = ToolKit.PreviewLines(text, If(state.Verbose, 200, If(ok, 4, 10)), "      ")}
        End Function

    End Class

    Friend NotInheritable Class BashOutputTool
        Implements ITool

        Private ReadOnly _shell As ShellManager

        Public Sub New(shell As ShellManager)
            _shell = shell
        End Sub

        Public ReadOnly Property Name As String = "BashOutput" Implements ITool.Name
        Public ReadOnly Property Description As String = "Audits a background command (run_in_background, or a Bash command still running after its wait): running or exited " &
            "(exit code), for how long, how long since it last printed something, its new output since the last read, and a warning when it seems to wait for input " &
            "or to be stuck. wait_seconds: first wait up to that long for it to finish or print more (instead of calling again and again). filter keeps only lines " &
            "matching a regular expression." Implements ITool.Description
        Public ReadOnly Property IsReadOnly As Boolean = True Implements ITool.IsReadOnly

        ''' <summary>A last line that asks a question and waits for an answer.</summary>
        Private Shared ReadOnly PromptLine As New Regex("(\(y/n\)|\[y/n\]|\(yes/no\)|\[yes/no\]|press any key|press enter|password\s*:?\s*$|passphrase|are you sure|" &
                                                        "do you want to (continue|proceed)|continue\?\s*$|\?\s*$)", RegexOptions.IgnoreCase)

        Public Function Parameters() As JsonObject Implements ITool.Parameters
            Return ToolKit.Schema({"bash_id"}, ("bash_id", "string", "The background shell's id"), ("filter", "string", "Regular expression; only matching lines"),
                                  ("wait_seconds", "integer", "Wait up to this long for it to finish or print more first (max 3600)"))
        End Function

        Public Function Title(input As JsonElement, state As AgentState) As String Implements ITool.Title
            Return "BashOutput(" & ToolKit.Arg(input, "bash_id", "") & ")"
        End Function

        Public Function Preview(input As JsonElement, state As AgentState) As String Implements ITool.Preview
            Return Nothing
        End Function

        Public Async Function RunAsync(input As JsonElement, ctx As ToolContext) As Task(Of ToolResult) Implements ITool.RunAsync
            Dim sh = _shell.Get(ToolKit.Arg(input, "bash_id", ToolKit.Arg(input, "shell_id", "")))
            If sh Is Nothing Then Return ToolResult.Fail("No background shell with that id. There are: " & If(_shell.All().Count = 0, "none", String.Join(", ", _shell.All().Select(Function(s) s.Id))))
            Dim rx As Regex = Nothing
            Dim f = ToolKit.Arg(input, "filter")
            If f IsNot Nothing Then
                Try
                    rx = New Regex(f)
                Catch ex As ArgumentException
                    Return ToolResult.Fail("Invalid filter: " & ex.Message)
                End Try
            End If
            Dim waitSeconds = Math.Min(3600, Math.Max(0, ToolKit.ArgInt(input, "wait_seconds", 0)))
            ' Waits only while there is nothing new to report.
            If waitSeconds > 0 AndAlso Not Done(sh) AndAlso OutputLength(sh) = sh.ReadPosition Then
                Dim from = OutputLength(sh)
                Dim deadline = DateTime.UtcNow.AddSeconds(waitSeconds)
                While Not Done(sh) AndAlso OutputLength(sh) = from AndAlso DateTime.UtcNow < deadline AndAlso Not ctx.Cancel.IsCancellationRequested
                    Await Task.Delay(200).ConfigureAwait(False)
                End While
                ' Output comes in pieces: a moment more for the rest of it.
                If Not Done(sh) AndAlso OutputLength(sh) > from Then Await Task.Delay(500).ConfigureAwait(False)
            End If
            Dim chunk = ShellManager.StripControl(ShellManager.TakeOutput(sh))
            ' The working-directory line a Bash command prints at its end is not output.
            chunk = Regex.Replace(chunk, "(?m)^__AGENTIC_CWD__=.*$\r?\n?", "")
            If rx IsNot Nothing Then chunk = String.Join(vbLf, chunk.Split(ChrW(10)).Where(Function(l) rx.IsMatch(l)))
            Dim now = DateTime.UtcNow
            Dim ended = ShellManager.ShellEnded(sh)
            Dim status = If(sh.Killed, "killed",
                         If(Not ended, $"running for {Span(now - sh.StartedUtc)}",
                            $"exited with code {sh.ExitCode} after {Span(sh.EndedUtc.Value - sh.StartedUtc)}" & If(sh.Closed, "", "; something it started is still running and printing")))
            If Not sh.Killed Then status &= If(sh.LastOutputUtc.HasValue, $"; last output {Span(now - sh.LastOutputUtc.Value)} ago", "; no output yet")
            Dim warning = If(sh.Killed OrElse ended, "", WarningFor(sh, now))
            Dim content = $"<status>{status}</status>" & vbLf & If(warning.Length > 0, $"<warning>{warning}</warning>" & vbLf, "") &
                          "<output>" & vbLf & ToolKit.Clip(If(chunk.Trim().Length = 0, "(no new output)", chunk.TrimEnd())) & vbLf & "</output>"
            Return ToolResult.Done(content, $"{sh.Id}: {status}", ToolKit.PreviewLines(chunk.Trim(), 4, "      "))
        End Function

        ''' <summary>Done: it ended and its output too, or its shell ended a moment ago (what still holds the output is not waited for).</summary>
        Private Shared Function Done(sh As BackgroundShell) As Boolean
            If sh.Killed Then Return True
            If Not ShellManager.ShellEnded(sh) Then Return False
            Return sh.Closed OrElse DateTime.UtcNow - sh.EndedUtc.Value >= TimeSpan.FromSeconds(2)
        End Function

        Private Shared Function OutputLength(sh As BackgroundShell) As Integer
            SyncLock sh.Output
                Return sh.Output.Length
            End SyncLock
        End Function

        ''' <summary>Something looks wrong with a command that is still running: a question waiting for an answer, or a long silence.</summary>
        Private Shared Function WarningFor(sh As BackgroundShell, now As DateTime) As String
            Dim idle = now - If(sh.LastOutputUtc, sh.StartedUtc)
            Dim last As String
            SyncLock sh.Output
                last = ShellManager.StripControl(sh.Output.ToString()).Split({vbCr, vbLf}, StringSplitOptions.RemoveEmptyEntries).Select(Function(l) l.Trim()).LastOrDefault(Function(l) l.Length > 0)
            End SyncLock
            If idle.TotalSeconds >= 5 AndAlso Not String.IsNullOrEmpty(last) AndAlso PromptLine.IsMatch(last) Then
                Return $"Its last line looks like a question waiting for an answer (""{If(last.Length > 160, last.Substring(last.Length - 160), last)}""). Commands get no " &
                       "keyboard input here: stop it with KillShell and run it so it does not ask (for example -y, --yes, -Force, -Confirm:$false, --no-input, -NonInteractive)."
            End If
            If idle.TotalMinutes >= 2 Then
                Return $"It has printed nothing for {Span(idle)}. It may be waiting for input, stuck, or busy without printing: if it should have printed something " &
                       "by now, stop it with KillShell and find out why."
            End If
            Return ""
        End Function

        ''' <summary>45 s, 3 min 20 s, 1 h 5 min.</summary>
        Friend Shared Function Span(t As TimeSpan) As String
            Dim s = CLng(Math.Max(0, Math.Round(t.TotalSeconds)))
            If s < 60 Then Return $"{s} s"
            If s < 3600 Then Return $"{s \ 60} min" & If(s Mod 60 > 0, $" {s Mod 60} s", "")
            Dim m = (s \ 60) Mod 60
            Return $"{s \ 3600} h" & If(m > 0, $" {m} min", "")
        End Function

    End Class

    Friend NotInheritable Class KillShellTool
        Implements ITool

        Private ReadOnly _shell As ShellManager

        Public Sub New(shell As ShellManager)
            _shell = shell
        End Sub

        Public ReadOnly Property Name As String = "KillShell" Implements ITool.Name
        Public ReadOnly Property Description As String = "Stops a background command started with run_in_background." Implements ITool.Description
        Public ReadOnly Property IsReadOnly As Boolean = False Implements ITool.IsReadOnly

        Public Function Parameters() As JsonObject Implements ITool.Parameters
            Return ToolKit.Schema({"shell_id"}, ("shell_id", "string", "The background shell's id"))
        End Function

        Public Function Title(input As JsonElement, state As AgentState) As String Implements ITool.Title
            Return "KillShell(" & ToolKit.Arg(input, "shell_id", "") & ")"
        End Function

        Public Function Preview(input As JsonElement, state As AgentState) As String Implements ITool.Preview
            Return Nothing
        End Function

        Public Function RunAsync(input As JsonElement, ctx As ToolContext) As Task(Of ToolResult) Implements ITool.RunAsync
            Dim id = ToolKit.Arg(input, "shell_id", ToolKit.Arg(input, "bash_id", ""))
            If _shell.Kill(id) Then Return Task.FromResult(ToolResult.Done($"Shell {id} was stopped.", "Stopped " & id))
            Return Task.FromResult(ToolResult.Fail($"No running background shell {id}."))
        End Function

    End Class

End Namespace
