Option Strict On
Option Explicit On
Option Infer On

Imports System.Collections.Concurrent
Imports System.Diagnostics
Imports System.IO
Imports System.Text
Imports System.Text.Json
Imports System.Text.Json.Nodes
Imports System.Threading
Imports AgenticCli.Core

Namespace Agent

    ''' <summary>
    ''' Claude Code (the claude CLI) running on this computer, used from the Agentic CLI's chat. Each message runs
    ''' "claude -p" in its streaming-JSON mode and continues the same Claude session (--resume), so Claude keeps its own
    ''' memory of the conversation, its tools, MCP servers, skills, slash commands, CLAUDE.md files and settings. Every
    ''' line Claude prints is passed to onLine unchanged; permission prompts (control_request can_use_tool) are answered
    ''' through Respond, like the official SDK does. This mirrors the desktop app's Claude Code integration.
    ''' </summary>
    Friend NotInheritable Class ClaudeCode

        ''' <summary>Settings for a run; the same knobs the desktop app exposes.</summary>
        Friend NotInheritable Class Options
            Public Property Model As String
            Public Property PermissionMode As String
            Public Property Effort As String
            Public Property Cwd As String
            Public Property FallbackModel As String
            Public Property AppendSystemPrompt As String
            Public Property MaxTurns As Integer
            Public Property AddDirs As New List(Of String)
            Public Property AllowedTools As New List(Of String)
            Public Property DisallowedTools As New List(Of String)
        End Class

        Friend NotInheritable Class Run
            Public Property Process As Process
            Public ReadOnly Gate As New SemaphoreSlim(1, 1)
            Public Property Done As Boolean
            Public Property SessionId As String
        End Class

        Private ReadOnly _runs As New ConcurrentDictionary(Of String, Run)(StringComparer.Ordinal)

        ''' <summary>The claude program: on PATH (the npm or native installer), the native installer's folder, or the copy inside the VS Code extension.</summary>
        Public Shared Function Find() As (Path As String, Source As String)
            Dim onPath = Platform.Which("claude")
            If onPath IsNot Nothing Then Return (onPath, "PATH")
            Dim home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            For Each p In {IO.Path.Combine(home, ".local", "bin", If(OperatingSystem.IsWindows(), "claude.exe", "claude")),
                           IO.Path.Combine(home, ".claude", "local", If(OperatingSystem.IsWindows(), "claude.exe", "claude"))}
                If File.Exists(p) Then Return (p, "native installer")
            Next
            For Each extRoot In {IO.Path.Combine(home, ".vscode", "extensions"), IO.Path.Combine(home, ".cursor", "extensions")}
                If Not Directory.Exists(extRoot) Then Continue For
                Dim newest = Directory.GetDirectories(extRoot, "anthropic.claude-code-*").OrderByDescending(Function(d) d, StringComparer.OrdinalIgnoreCase).
                    Select(Function(d) IO.Path.Combine(d, "resources", "native-binary", If(OperatingSystem.IsWindows(), "claude.exe", "claude"))).FirstOrDefault(AddressOf File.Exists)
                If newest IsNot Nothing Then Return (newest, "VS Code extension")
            Next
            Return (Nothing, Nothing)
        End Function

        Public Shared Function IsInstalled() As Boolean
            Return Find().Path IsNot Nothing
        End Function

        ''' <summary>How to start it: an npm .cmd shim is run through its cli.js with node (cmd.exe would mangle the arguments).</summary>
        Private Shared Function StartInfo(exe As String) As ProcessStartInfo
            If exe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) OrElse exe.EndsWith(".bat", StringComparison.OrdinalIgnoreCase) Then
                Dim dir = IO.Path.GetDirectoryName(exe)
                Dim cli = IO.Path.Combine(dir, "node_modules", "@anthropic-ai", "claude-code", "cli.js")
                Dim bin = IO.Path.Combine(dir, "node_modules", "@anthropic-ai", "claude-code", "bin", "claude.exe")
                If File.Exists(bin) Then Return New ProcessStartInfo(bin)
                If File.Exists(cli) Then
                    Dim psi As New ProcessStartInfo(If(Platform.Which("node"), "node"))
                    psi.ArgumentList.Add(cli)
                    Return psi
                End If
                Dim viaCmd As New ProcessStartInfo("cmd.exe")
                viaCmd.ArgumentList.Add("/d") : viaCmd.ArgumentList.Add("/s") : viaCmd.ArgumentList.Add("/c") : viaCmd.ArgumentList.Add(exe)
                Return viaCmd
            End If
            Return New ProcessStartInfo(exe)
        End Function

        Public Shared Async Function StatusAsync() As Task(Of (Installed As Boolean, Path As String, Source As String, Version As String, Problem As String))
            Dim found = Find()
            If found.Path Is Nothing Then Return (False, Nothing, Nothing, Nothing, Nothing)
            Dim version As String = Nothing
            Try
                Dim psi = StartInfo(found.Path)
                psi.ArgumentList.Add("--version")
                psi.RedirectStandardOutput = True : psi.RedirectStandardError = True : psi.UseShellExecute = False : psi.CreateNoWindow = True
                Using p = Process.Start(psi)
                    Dim read = p.StandardOutput.ReadToEndAsync()
                    If Await Task.WhenAny(read, Task.Delay(15000)).ConfigureAwait(False) Is read Then version = (Await read.ConfigureAwait(False)).Trim()
                    If Not p.HasExited Then p.Kill(True)
                End Using
            Catch ex As Exception When TypeOf ex Is ComponentModel.Win32Exception OrElse TypeOf ex Is InvalidOperationException OrElse TypeOf ex Is IOException
                Return (False, found.Path, found.Source, Nothing, ex.Message)
            End Try
            Return (True, found.Path, found.Source, version, Nothing)
        End Function

        ''' <summary>
        ''' Runs one message. Each line Claude prints is passed to onLine; the task ends when Claude exits. plan forces
        ''' the plan permission mode. sessionId continues an earlier Claude session (--resume).
        ''' </summary>
        Public Async Function RunAsync(runId As String, prompt As String, sessionId As String, plan As Boolean, opts As Options,
                                       onLine As Func(Of String, Run, Task), ct As CancellationToken) As Task
            Dim found = Find()
            If found.Path Is Nothing Then Throw New InvalidOperationException("Claude Code is not installed on this computer (the claude command was not found).")
            If String.IsNullOrWhiteSpace(runId) Then runId = Guid.NewGuid().ToString("N")
            Dim cwd = opts?.Cwd
            If String.IsNullOrWhiteSpace(cwd) OrElse Not Directory.Exists(cwd) Then cwd = Environment.CurrentDirectory

            Dim psi = StartInfo(found.Path)
            AddArgs(psi, "-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose", "--include-partial-messages", "--permission-prompt-tool", "stdio")
            Dim sessionGuid As Guid
            If Not String.IsNullOrWhiteSpace(sessionId) AndAlso Guid.TryParse(sessionId, sessionGuid) Then AddArgs(psi, "--resume", sessionId)
            If opts IsNot Nothing Then
                If Not String.IsNullOrWhiteSpace(opts.Model) AndAlso opts.Model <> "default" Then AddArgs(psi, "--model", opts.Model)
                Dim mode = If(plan, "plan", opts.PermissionMode)
                If Not String.IsNullOrWhiteSpace(mode) AndAlso mode <> "default" Then AddArgs(psi, "--permission-mode", mode)
                If Not String.IsNullOrWhiteSpace(opts.Effort) AndAlso opts.Effort <> "default" Then AddArgs(psi, "--effort", opts.Effort)
                For Each d In opts.AddDirs.Where(AddressOf Directory.Exists)
                    AddArgs(psi, "--add-dir", d)
                Next
                If Not String.IsNullOrWhiteSpace(opts.AppendSystemPrompt) Then AddArgs(psi, "--append-system-prompt", opts.AppendSystemPrompt)
                If opts.MaxTurns > 0 Then AddArgs(psi, "--max-turns", opts.MaxTurns.ToString(Globalization.CultureInfo.InvariantCulture))
                If Not String.IsNullOrWhiteSpace(opts.FallbackModel) Then AddArgs(psi, "--fallback-model", opts.FallbackModel)
                If opts.AllowedTools.Count > 0 Then AddArgs(psi, "--allowedTools", String.Join(",", opts.AllowedTools))
                If opts.DisallowedTools.Count > 0 Then AddArgs(psi, "--disallowedTools", String.Join(",", opts.DisallowedTools))
            ElseIf plan Then
                AddArgs(psi, "--permission-mode", "plan")
            End If
            psi.WorkingDirectory = cwd
            psi.RedirectStandardInput = True : psi.RedirectStandardOutput = True : psi.RedirectStandardError = True
            psi.UseShellExecute = False : psi.CreateNoWindow = True
            psi.StandardOutputEncoding = Encoding.UTF8 : psi.StandardErrorEncoding = Encoding.UTF8
            ' Started from inside another Claude Code session (a developer's terminal), claude would refuse to run.
            psi.Environment.Remove("CLAUDECODE")
            psi.Environment("CLAUDE_CODE_ENTRYPOINT") = "agentic-cli"

            Dim content As New JsonArray()
            content.Add(New JsonObject From {{"type", "text"}, {"text", If(prompt, "")}})

            Dim r As New Run()
            Dim p As Process
            Try
                p = Process.Start(psi)
            Catch ex As ComponentModel.Win32Exception
                Throw New InvalidOperationException("Could not start Claude Code: " & ex.Message)
            End Try
            r.Process = p
            _runs(runId) = r
            Dim stderr As New StringBuilder()
            AddHandler p.ErrorDataReceived, Sub(sender, e)
                                                If e.Data Is Nothing Then Return
                                                SyncLock stderr
                                                    If stderr.Length < 20000 Then stderr.AppendLine(e.Data)
                                                End SyncLock
                                            End Sub
            p.BeginErrorReadLine()
            Using reg = ct.Register(Sub() Kill(r))
                Try
                    Await WriteLineAsync(r, New JsonObject From {{"type", "user"}, {"message", New JsonObject From {{"role", "user"}, {"content", content}}}}.ToJsonString()).ConfigureAwait(False)
                    While True
                        Dim line = Await p.StandardOutput.ReadLineAsync(ct).ConfigureAwait(False)
                        If line Is Nothing Then Exit While
                        If line.Length = 0 Then Continue While
                        CaptureSession(r, line)
                        ' The answer is complete: close stdin so claude exits (it waits for more messages otherwise).
                        If IsResult(line) Then
                            r.Done = True
                            Await onLine(line, r).ConfigureAwait(False)
                            Try
                                p.StandardInput.Close()
                            Catch ex As IOException
                            End Try
                            Continue While
                        End If
                        Await onLine(line, r).ConfigureAwait(False)
                    End While
                    Await p.WaitForExitAsync(ct).ConfigureAwait(False)
                    If Not r.Done Then
                        Dim err As String
                        SyncLock stderr
                            err = stderr.ToString().Trim()
                        End SyncLock
                        Throw New InvalidOperationException(If(err.Length > 0, err.Substring(Math.Max(0, err.Length - 3000)), $"Claude Code stopped (exit code {p.ExitCode})."))
                    End If
                Finally
                    Kill(r)
                    Dim gone As Run = Nothing
                    _runs.TryRemove(runId, gone)
                    p.Dispose()
                End Try
            End Using
        End Function

        ''' <summary>Answers a control request (a permission prompt). response is the raw JSON the SDK expects.</summary>
        Public Async Function RespondAsync(r As Run, requestId As String, response As JsonNode) As Task
            Dim msg As New JsonObject From {{"type", "control_response"}, {"response", New JsonObject From {
                {"subtype", "success"}, {"request_id", requestId}, {"response", response}}}}
            Await WriteLineAsync(r, msg.ToJsonString()).ConfigureAwait(False)
        End Function

        Private Shared Sub CaptureSession(r As Run, line As String)
            If r.SessionId IsNot Nothing Then Return
            If line.IndexOf("""session_id""", StringComparison.Ordinal) < 0 Then Return
            Try
                Using doc = JsonDocument.Parse(line)
                    Dim sid As JsonElement
                    If doc.RootElement.ValueKind = JsonValueKind.Object AndAlso doc.RootElement.TryGetProperty("session_id", sid) AndAlso sid.ValueKind = JsonValueKind.String Then r.SessionId = sid.GetString()
                End Using
            Catch ex As JsonException
            End Try
        End Sub

        Private Shared Sub AddArgs(psi As ProcessStartInfo, ParamArray values As String())
            For Each v In values
                psi.ArgumentList.Add(v)
            Next
        End Sub

        ''' <summary>The line that ends an answer: {"type":"result", …} (its keys come in any order).</summary>
        Friend Shared Function IsResult(line As String) As Boolean
            If line.IndexOf("""result""", StringComparison.Ordinal) < 0 Then Return False
            Try
                Using doc = JsonDocument.Parse(line)
                    Dim t As JsonElement
                    Return doc.RootElement.ValueKind = JsonValueKind.Object AndAlso doc.RootElement.TryGetProperty("type", t) AndAlso t.ValueKind = JsonValueKind.String AndAlso t.GetString() = "result"
                End Using
            Catch ex As JsonException
                Return False
            End Try
        End Function

        Private Shared Async Function WriteLineAsync(r As Run, line As String) As Task
            Await r.Gate.WaitAsync().ConfigureAwait(False)
            Try
                Await r.Process.StandardInput.WriteLineAsync(line).ConfigureAwait(False)
                Await r.Process.StandardInput.FlushAsync().ConfigureAwait(False)
            Finally
                r.Gate.Release()
            End Try
        End Function

        Private Shared Sub Kill(r As Run)
            Try
                If r.Process IsNot Nothing AndAlso Not r.Process.HasExited Then r.Process.Kill(True)
            Catch ex As Exception When TypeOf ex Is InvalidOperationException OrElse TypeOf ex Is ComponentModel.Win32Exception
            End Try
        End Sub

    End Class

End Namespace
