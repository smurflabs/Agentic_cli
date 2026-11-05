Option Strict On
Option Explicit On
Option Infer On

Imports System.IO
Imports System.IO.Compression
Imports System.Net.Http
Imports AgenticCli.Tools

''' <summary>Self-tests of /run: how projects run (launch.json, detection), runs and their addresses, the static server, sharing's zip.</summary>
Partial Friend NotInheritable Class SelfTest

    Private Shared Sub ProjectRunTests(tmp As String)
        Dim root = Path.Combine(tmp, "runproj")
        Directory.CreateDirectory(Path.Combine(root, ".agentic"))
        Directory.CreateDirectory(Path.Combine(root, "dist", "node_modules"))
        File.WriteAllText(Path.Combine(root, "package.json"), "{""scripts"": {""dev"": ""vite"", ""build"": ""vite build"", ""test"": ""vitest""}}")
        File.WriteAllText(Path.Combine(root, "pnpm-lock.yaml"), "")
        File.WriteAllText(Path.Combine(root, "index.html"), "<script type=module src=/src/main.js></script>")
        Dim detected = ProjectRuns.DetectRunConfigs(root)
        Check("run: package.json scripts with the lock file's package manager",
              detected.Select(Function(c) c.Command).SequenceEqual({"pnpm run dev", "pnpm run build", "pnpm run test"}), String.Join(", ", detected.Select(Function(c) c.Command)))
        Check("run: an app with a build shares nothing before it is built", ProjectRuns.ShareFolder(root) Is Nothing)
        File.WriteAllText(Path.Combine(root, "dist", "index.html"), "<title>Built</title>")
        File.WriteAllText(Path.Combine(root, "dist", "app.js"), "export {}")
        File.WriteAllText(Path.Combine(root, "dist", "node_modules", "x.js"), "no")
        File.WriteAllText(Path.Combine(root, "dist", ".env"), "SECRET=1")
        Check("run: then its build", ProjectRuns.ShareFolder(root) = "dist")
        Using ms As New MemoryStream(ProjectRuns.ZipSite(Path.Combine(root, "dist"), 1024 * 1024)), z As New ZipArchive(ms)
            Dim names = z.Entries.Select(Function(e) e.FullName).OrderBy(Function(n) n).ToList()
            Check("run: the zip has the site's files only", names.SequenceEqual({"app.js", "index.html"}), String.Join(",", names))
        End Using

        ' launch.json wins (Claude Code's format), a second folder's ids start with its name.
        Dim web = Path.Combine(tmp, "runweb")
        Directory.CreateDirectory(Path.Combine(web, ".claude"))
        Dim port = FreeTcpPort()
        File.WriteAllText(Path.Combine(web, "server.js"), $"require('http').createServer((q, r) => r.end('hi from dev')).listen({port}, '127.0.0.1', () => console.log('\x1b[32m  Local:   http://localhost:{port}/\x1b[0m'));")
        File.WriteAllText(Path.Combine(web, "index.html"), "<p>static</p>")
        File.WriteAllText(Path.Combine(web, ".claude", "launch.json"), "{ // dev" & vbLf & """configurations"": [{""name"": ""Dev"", ""runtimeExecutable"": ""node"", ""runtimeArgs"": [""server.js""]}," &
                          "{""name"": ""Unit tests"", ""command"": ""node -e \""console.log('2 failed'); process.exit(3)\""""}, {""name"": ""Site"", ""kind"": ""static""}],}")
        Dim configs = ProjectRuns.Configs({root, web})
        Dim ids = configs.Select(Function(c) c.Id).ToList()
        Check("run: ids for every project", ids.SequenceEqual({"dev-server", "build", "tests", "runweb-dev", "runweb-unit-tests", "runweb-site"}), String.Join(",", ids))
        Dim dev = configs.First(Function(c) c.Id = "runweb-dev")
        Check("run: runtimeExecutable and its arguments", dev.Command = "node server.js" AndAlso dev.Source = ".claude/launch.json" AndAlso dev.Kind = "app", $"{dev.Command} {dev.Source} {dev.Kind}")
        Check("run: a test kind from its name", configs.First(Function(c) c.Id = "runweb-unit-tests").Kind = "test")

        Dim runs As New ProjectRuns(New ShellManager("auto"))
        Try
            ' A dev server: its address from its output (colour codes and all).
            Dim run = runs.Start(dev)
            For i = 1 To 150
                ProjectRuns.ScanForUrl(run)
                If run.Url IsNot Nothing OrElse Not run.Running Then Exit For
                Threading.Thread.Sleep(100)
            Next
            Check("run: a dev server's address from its output", run.Url = $"http://localhost:{port}/", If(run.Url, ProjectRuns.OutputFrom(run, 0).Text))
            Dim page As String = Nothing
            Try
                Using http As New HttpClient With {.Timeout = TimeSpan.FromSeconds(5)}
                    page = http.GetStringAsync($"http://127.0.0.1:{port}/").GetAwaiter().GetResult()
                End Using
            Catch ex As HttpRequestException
                page = ex.Message
            End Try
            Check("run: it answers", page = "hi from dev", page)
            Dim shown = ProjectRuns.OutputFrom(run, 0).Text
            Check("run: its output has no colour codes or the shell's bookkeeping", shown.Contains("Local:") AndAlso Not shown.Contains(ChrW(27)) AndAlso Not shown.Contains("__AGENTIC_CWD__"), shown)
            Check("run: stop", runs.Stop(dev.Id) AndAlso Not run.Running)

            ' Tests: they end with their own exit code.
            Dim tests = runs.Start(configs.First(Function(c) c.Id = "runweb-unit-tests"))
            For i = 1 To 150
                If Not tests.Running Then Exit For
                Threading.Thread.Sleep(100)
            Next
            Check("run: tests end with their exit code", tests.ExitCode.GetValueOrDefault(-99) = 3, $"{tests.ExitCode}")
            Check("run: and their output", ProjectRuns.OutputFrom(tests, 0).Text.Contains("2 failed"))

            ' The built-in server for a static site.
            Dim site = runs.Start(configs.First(Function(c) c.Id = "runweb-site"))
            Dim html As String = Nothing, missing As Net.HttpStatusCode
            Using http As New HttpClient With {.Timeout = TimeSpan.FromSeconds(5)}
                html = http.GetStringAsync(site.Url).GetAwaiter().GetResult()
                missing = http.GetAsync(site.Url & "..%2F..%2Fsecret.txt").GetAwaiter().GetResult().StatusCode
            End Using
            Check("run: the static site is served", html = "<p>static</p>" AndAlso site.Url.StartsWith("http://127.0.0.1:"), html)
            Check("run: nothing outside its folder", missing <> Net.HttpStatusCode.OK)
            Check("run: stop all", runs.StopAll() = 1 AndAlso Not site.Running)
        Finally
            runs.StopAll()
        End Try
        Dim launch = ProjectRuns.EnsureLaunchFile(root)
        Check("run: /run edit writes launch.json from what was found", File.ReadAllText(launch).Contains("pnpm run dev"))
    End Sub

    Private Shared Function FreeTcpPort() As Integer
        Dim l As New Net.Sockets.TcpListener(Net.IPAddress.Loopback, 0)
        l.Start()
        Dim p = DirectCast(l.LocalEndpoint, Net.IPEndPoint).Port
        l.Stop()
        Return p
    End Function

End Class
