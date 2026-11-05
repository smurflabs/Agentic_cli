Option Strict On
Option Explicit On
Option Infer On

Imports System.IO
Imports System.Text.Json
Imports System.Text.RegularExpressions
Imports AgenticCli.Core
Imports AgenticCli.Tools

Namespace Agent

    Friend Enum Verdict
        Allow
        Deny
        Ask
    End Enum

    ''' <summary>A rule like Claude Code's: "Bash(npm run test:*)", "Edit(src/**)", "Read(//c/secrets/**)", "WebFetch(domain:github.com)", "mcp__github".</summary>
    Friend NotInheritable Class PermissionRule
        Public Property Raw As String
        Public Property Tool As String
        Public Property Spec As String
        Public Property Source As String

        Public Shared Function Parse(raw As String, source As String) As PermissionRule
            Dim r As New PermissionRule With {.Raw = raw.Trim(), .Source = source}
            Dim m = Regex.Match(r.Raw, "^([\w\-\*]+)\s*\((.*)\)\s*$", RegexOptions.Singleline)
            If m.Success Then
                r.Tool = m.Groups(1).Value
                r.Spec = m.Groups(2).Value.Trim()
                If r.Spec = "" OrElse r.Spec = "*" Then r.Spec = Nothing
            Else
                r.Tool = r.Raw
            End If
            Return r
        End Function

        Public Overrides Function ToString() As String
            Return Raw
        End Function
    End Class

    Friend NotInheritable Class PermissionCheck
        Public Property Verdict As Verdict
        Public Property Reason As String
        Public Property Rule As PermissionRule
    End Class

    ''' <summary>
    ''' Decides whether a tool call runs, is refused, or needs the user's answer — like Claude Code:
    '''   deny rules → refused; plan mode → only reading; ask rules → ask; bypassPermissions → runs; allow rules → runs;
    '''   reading inside the working folders → runs; edits → run in acceptEdits mode, else ask; read-only commands → run;
    '''   everything else → ask.
    ''' </summary>
    Friend NotInheritable Class PermissionEngine

        Private ReadOnly _state As AgentState
        ''' <summary>"Yes, and don't ask again" answers in this session, and --allowedTools.</summary>
        Public ReadOnly Property SessionAllow As New List(Of PermissionRule)
        ''' <summary>--disallowedTools.</summary>
        Public ReadOnly Property SessionDeny As New List(Of PermissionRule)

        Public Sub New(state As AgentState)
            _state = state
        End Sub

        Public Function Rules(kind As String) As List(Of PermissionRule)
            Dim list = _state.Settings.Entries("permissions." & kind).Select(Function(e) PermissionRule.Parse(e.Value, e.Scope)).ToList()
            If kind = "allow" Then
                SyncLock SessionAllow
                    list.AddRange(SessionAllow)
                End SyncLock
            End If
            If kind = "deny" Then list.AddRange(SessionDeny)
            Return list
        End Function

        ''' <summary>Adds a rule for this session. Parallel tool calls read the rules while another call's question is answered.</summary>
        Public Sub AllowForSession(rule As PermissionRule)
            SyncLock SessionAllow
                SessionAllow.Add(rule)
            End SyncLock
        End Sub

        Private Shared ReadOnly EditTools As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase) From {"Write", "Edit", "MultiEdit", "NotebookEdit"}
        Private Shared ReadOnly ReadTools As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase) From {"Read", "Glob", "Grep", "LS"}

        Public Shared Function IsEditTool(name As String) As Boolean
            Return EditTools.Contains(name)
        End Function

        Public Function Evaluate(tool As ITool, input As JsonElement) As PermissionCheck
            Dim name = tool.Name
            Dim deny = Rules("deny").FirstOrDefault(Function(r) Matches(r, name, input))
            If deny IsNot Nothing Then Return New PermissionCheck With {.Verdict = Verdict.Deny, .Rule = deny, .Reason = $"denied by the rule {deny.Raw} ({deny.Source} settings)"}
            If _state.Mode = PermissionMode.Plan AndAlso Not tool.IsReadOnly Then
                Return New PermissionCheck With {.Verdict = Verdict.Deny, .Reason = "plan mode is on: only reading and research until the plan is approved (ExitPlanMode)"}
            End If
            Dim ask = Rules("ask").FirstOrDefault(Function(r) Matches(r, name, input))
            If ask IsNot Nothing AndAlso _state.Mode <> PermissionMode.BypassPermissions Then Return New PermissionCheck With {.Verdict = Verdict.Ask, .Rule = ask, .Reason = "the rule " & ask.Raw & " asks first"}
            If _state.Mode = PermissionMode.BypassPermissions Then Return New PermissionCheck With {.Verdict = Verdict.Allow, .Reason = "bypass permissions mode"}
            Dim allow = Rules("allow").FirstOrDefault(Function(r) Matches(r, name, input))
            If allow IsNot Nothing Then Return New PermissionCheck With {.Verdict = Verdict.Allow, .Rule = allow, .Reason = "allowed by " & allow.Raw}

            Dim target = TargetPath(name, input)
            If ReadTools.Contains(name) Then
                If target Is Nothing OrElse _state.InWorkingDirs(target) Then Return New PermissionCheck With {.Verdict = Verdict.Allow, .Reason = "reading in the working folders"}
                Return New PermissionCheck With {.Verdict = Verdict.Ask, .Reason = "it reads outside the working folders"}
            End If
            If EditTools.Contains(name) Then
                If target IsNot Nothing AndAlso Not _state.InWorkingDirs(target) Then Return New PermissionCheck With {.Verdict = Verdict.Ask, .Reason = "it changes a file outside the working folders"}
                If _state.Mode = PermissionMode.AcceptEdits Then Return New PermissionCheck With {.Verdict = Verdict.Allow, .Reason = "accept edits mode"}
                Return New PermissionCheck With {.Verdict = Verdict.Ask, .Reason = "it changes a file"}
            End If
            If name = "Bash" OrElse name = "Monitor" Then
                Dim cmdText = Jx.StrOf(input, "command", "")
                If IsReadOnlyCommand(cmdText) Then Return New PermissionCheck With {.Verdict = Verdict.Allow, .Reason = "read-only command"}
                Return New PermissionCheck With {.Verdict = Verdict.Ask, .Reason = "it runs a command"}
            End If
            If name = "WebFetch" Then Return New PermissionCheck With {.Verdict = Verdict.Ask, .Reason = "it opens " & HostOf(Jx.StrOf(input, "url", ""))}
            If tool.IsReadOnly Then Return New PermissionCheck With {.Verdict = Verdict.Allow, .Reason = "read-only tool"}
            If name = "KillShell" Then Return New PermissionCheck With {.Verdict = Verdict.Allow, .Reason = "stops this session's own command"}
            Return New PermissionCheck With {.Verdict = Verdict.Ask, .Reason = If(name.StartsWith("mcp__"), "it uses an MCP tool", "it uses " & name)}
        End Function

        ' ------------------------------------------------------------------ matching

        Public Function Matches(rule As PermissionRule, toolName As String, input As JsonElement) As Boolean
            ' Monitor runs a command like Bash: Bash rules cover it too.
            If Not ToolMatches(rule.Tool, toolName) AndAlso Not (toolName = "Monitor" AndAlso ToolMatches(rule.Tool, "Bash")) Then Return False
            If rule.Spec Is Nothing Then Return True
            If toolName = "Bash" OrElse toolName = "Monitor" Then Return BashMatches(rule.Spec, Jx.StrOf(input, "command", ""))
            If toolName = "WebFetch" Then
                Dim host = HostOf(Jx.StrOf(input, "url", ""))
                Dim spec = rule.Spec
                If spec.StartsWith("domain:", StringComparison.OrdinalIgnoreCase) Then spec = spec.Substring(7)
                Return host.Equals(spec, StringComparison.OrdinalIgnoreCase) OrElse host.EndsWith("." & spec, StringComparison.OrdinalIgnoreCase)
            End If
            If toolName = "Task" Then Return Jx.StrOf(input, "subagent_type", "").Equals(rule.Spec, StringComparison.OrdinalIgnoreCase)
            Dim target = TargetPath(toolName, input)
            If target Is Nothing Then Return False
            Return PathMatches(rule.Spec, target)
        End Function

        Private Shared Function ToolMatches(ruleTool As String, toolName As String) As Boolean
            If ruleTool = "*" Then Return True
            If ruleTool.Equals(toolName, StringComparison.OrdinalIgnoreCase) Then Return True
            ' Edit rules cover every tool that changes files; Read rules cover the search tools.
            If ruleTool.Equals("Edit", StringComparison.OrdinalIgnoreCase) AndAlso EditTools.Contains(toolName) Then Return True
            If ruleTool.Equals("Read", StringComparison.OrdinalIgnoreCase) AndAlso ReadTools.Contains(toolName) Then Return True
            ' mcp__server matches all of the server's tools; mcp__server__* too.
            If ruleTool.StartsWith("mcp__", StringComparison.OrdinalIgnoreCase) Then
                Dim prefix = ruleTool.TrimEnd("*"c).TrimEnd("_"c)
                Return toolName.StartsWith(prefix & "__", StringComparison.OrdinalIgnoreCase) OrElse toolName.Equals(prefix, StringComparison.OrdinalIgnoreCase)
            End If
            Return False
        End Function

        ''' <summary>"npm run test:*" matches commands starting with "npm run test"; otherwise the command must be exactly the spec.</summary>
        Public Shared Function BashMatches(spec As String, cmdText As String) As Boolean
            Dim c = NormalizeCommand(cmdText)
            Dim parts = SplitCommand(c)
            If parts.Count > 1 Then Return parts.All(Function(p) BashMatchesOne(spec, p))
            Return BashMatchesOne(spec, c)
        End Function

        Private Shared Function BashMatchesOne(spec As String, c As String) As Boolean
            If spec.EndsWith(":*") Then
                ' "npm run test:*" covers "npm run test", "npm run test -- --watch" and "npm run test:unit", not "npm run testx".
                Dim prefix = NormalizeCommand(spec.Substring(0, spec.Length - 2))
                If Not c.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) Then Return False
                Return c.Length = prefix.Length OrElse c(prefix.Length) = " "c OrElse c(prefix.Length) = ":"c
            End If
            If spec.EndsWith("*") Then Return c.StartsWith(NormalizeCommand(spec.TrimEnd("*"c)), StringComparison.OrdinalIgnoreCase)
            Return c.Equals(NormalizeCommand(spec), StringComparison.OrdinalIgnoreCase)
        End Function

        Private Shared Function NormalizeCommand(c As String) As String
            Return Regex.Replace(If(c, "").Trim(), "\s+", " ")
        End Function

        ''' <summary>Splits a command line on &amp;&amp;, ||, ;, | and newlines (outside quotes).</summary>
        Public Shared Function SplitCommand(c As String) As List(Of String)
            Dim parts As New List(Of String)
            Dim cur As New Text.StringBuilder()
            Dim quote As Char = ChrW(0)
            Dim i = 0
            While i < c.Length
                Dim ch = c(i)
                If quote <> ChrW(0) Then
                    cur.Append(ch)
                    If ch = quote Then quote = ChrW(0)
                    i += 1
                    Continue While
                End If
                If ch = """"c OrElse ch = "'"c Then
                    quote = ch
                    cur.Append(ch)
                    i += 1
                    Continue While
                End If
                Dim two = If(i + 1 < c.Length, c.Substring(i, 2), "")
                If two = "&&" OrElse two = "||" Then
                    parts.Add(cur.ToString())
                    cur.Clear()
                    i += 2
                    Continue While
                End If
                ' 2>&1 and &> are redirections, not a command in the background.
                Dim redirect = ch = "&"c AndAlso ((i > 0 AndAlso c(i - 1) = ">"c) OrElse (i + 1 < c.Length AndAlso c(i + 1) = ">"c))
                If Not redirect AndAlso (ch = ";"c OrElse ch = "|"c OrElse ch = ChrW(10) OrElse ch = "&"c) Then
                    parts.Add(cur.ToString())
                    cur.Clear()
                    i += 1
                    Continue While
                End If
                cur.Append(ch)
                i += 1
            End While
            parts.Add(cur.ToString())
            Return parts.Select(Function(p) NormalizeCommand(p)).Where(Function(p) p.Length > 0).ToList()
        End Function

        Private Shared ReadOnly SafeFirst As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase) From {
            "ls", "dir", "pwd", "cd", "echo", "cat", "type", "head", "tail", "wc", "grep", "rg", "findstr", "where", "which", "whoami", "hostname", "date", "tree",
            "Get-ChildItem", "gci", "Get-Content", "gc", "Get-Location", "gl", "Select-String", "sls", "Get-Item", "gi", "Test-Path", "Resolve-Path", "Get-Command", "gcm",
            "Measure-Object", "Sort-Object", "Select-Object", "Format-Table", "Format-List", "Where-Object", "Get-Date", "Write-Output", "Write-Host", "sort", "uniq", "cut", "file", "stat", "du", "df",
            "Get-Process", "ps", "Get-Service", "Get-ItemProperty", "Split-Path", "Join-Path", "Out-String", "ConvertTo-Json", "ConvertFrom-Json", "basename", "dirname", "realpath", "printenv"}
        Private Shared ReadOnly SafeGit As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase) From {
            "status", "diff", "log", "show", "branch", "remote", "rev-parse", "describe", "blame", "ls-files", "shortlog", "reflog", "tag", "config", "grep", "show-ref", "ls-tree", "cat-file", "merge-base", "whatchanged"}

        ''' <summary>An option among a command's arguments: "--output", or "--output=x".</summary>
        Private Shared Function HasFlag(args As List(Of String), ParamArray flags As String()) As Boolean
            Return args.Any(Function(t) flags.Any(Function(f) t.Equals(f, StringComparison.Ordinal) OrElse t.StartsWith(f & "=", StringComparison.Ordinal)))
        End Function

        ''' <summary>Redirections of error output that write no file: 2>&amp;1, 2>/dev/null, 2>$null, 2>nul.</summary>
        Private Shared ReadOnly HarmlessRedirect As New Regex("(?<![\w>])2>\s*(?:&1|/dev/null|\$null|nul)(?![\w/.])", RegexOptions.IgnoreCase)

        ''' <summary>
        ''' Commands that only read (ls, git status, Get-Content…) run without asking; redirection or anything unknown asks. A command
        ''' named with a path (.\cat.ps1, C:\tmp\ls.exe) runs that file whatever its name, so it asks; so does anything that can run
        ''' code inside an argument (PowerShell's ( ) and { } blocks, [Type]::Method(), dot-sourcing, $( ) and backticks).
        ''' </summary>
        Public Shared Function IsReadOnlyCommand(cmdText As String) As Boolean
            Dim c = If(cmdText, "").Trim()
            If c.Length = 0 Then Return False
            ' Writing to files, subshells and expansions can do anything.
            If Regex.IsMatch(c, "\$\(|`[^`]*`|\b(Out-File|Set-Content|Add-Content|Remove-Item|New-Item|Move-Item|Copy-Item|Rename-Item|Invoke-Expression|iex|Start-Process|Tee-Object)\b", RegexOptions.IgnoreCase) Then Return False
            Dim unquoted = Regex.Replace(HarmlessRedirect.Replace(c, " "), "'[^']*'|""[^""]*""", "''")
            If unquoted.Contains(">"c) OrElse unquoted.Contains("("c) OrElse unquoted.Contains("{"c) OrElse unquoted.Contains("]::") Then Return False
            If Regex.IsMatch(unquoted, "(^|[;|&])\s*\.\s") Then Return False
            For Each part In SplitCommand(c)
                Dim tokens = Regex.Matches(part, "(""[^""]*""|'[^']*'|\S+)").Select(Function(m) m.Value).ToList()
                If tokens.Count = 0 Then Continue For
                Dim named = tokens(0).Trim(""""c, "'"c)
                If named.IndexOfAny({"\"c, "/"c}) >= 0 Then Return False
                Dim first = If(named.EndsWith(".exe", StringComparison.OrdinalIgnoreCase), named.Substring(0, named.Length - 4), named)
                Dim args = tokens.Skip(1).ToList()
                Dim positional = args.Where(Function(t) Not t.StartsWith("-")).ToList()
                ' git log --output=x, sort --output x write files.
                If HasFlag(args,"--output") Then Return False
                If first.Equals("git", StringComparison.OrdinalIgnoreCase) Then
                    Dim subcmd = positional.FirstOrDefault()
                    If subcmd Is Nothing OrElse Not SafeGit.Contains(subcmd) Then Return False
                    Dim rest = positional.Skip(1).ToList()
                    Select Case subcmd.ToLowerInvariant()
                        Case "branch"
                            If HasFlag(args,"-d", "-D", "-m", "-M", "-c", "-C", "--delete", "--move", "--copy", "-u", "--set-upstream-to", "--unset-upstream", "--edit-description", "-f", "--force") Then Return False
                            ' git branch name creates a branch; listing takes patterns only with --list.
                            If rest.Count > 0 AndAlso Not HasFlag(args,"-l", "--list", "--contains", "--no-contains", "--merged", "--no-merged", "--points-at") Then Return False
                        Case "tag"
                            If tokens.Count > 2 AndAlso Not HasFlag(args,"-l", "--list") Then Return False
                        Case "config"
                            If Not HasFlag(args,"--get", "--list", "-l", "--get-all", "--get-regexp") Then Return False
                        Case "remote"
                            If tokens.Count > 2 AndAlso Not HasFlag(args,"-v", "--verbose") AndAlso Not If(rest.FirstOrDefault(), "").Equals("show", StringComparison.OrdinalIgnoreCase) Then Return False
                        Case "reflog"
                            If rest.Any(Function(t) t.Equals("expire", StringComparison.OrdinalIgnoreCase) OrElse t.Equals("delete", StringComparison.OrdinalIgnoreCase)) Then Return False
                        Case "grep"
                            If HasFlag(args,"-O", "--open-files-in-pager") OrElse args.Any(Function(t) t.StartsWith("-O", StringComparison.Ordinal)) Then Return False
                    End Select
                    Continue For
                End If
                If first.Equals("dotnet", StringComparison.OrdinalIgnoreCase) OrElse first.Equals("node", StringComparison.OrdinalIgnoreCase) OrElse
                   first.Equals("npm", StringComparison.OrdinalIgnoreCase) OrElse first.Equals("python", StringComparison.OrdinalIgnoreCase) OrElse first.Equals("py", StringComparison.OrdinalIgnoreCase) Then
                    If tokens.Count = 2 AndAlso (tokens(1) = "--version" OrElse tokens(1) = "--info" OrElse tokens(1) = "-v" OrElse tokens(1) = "--list-sdks" OrElse tokens(1) = "-V") Then Continue For
                    Return False
                End If
                If Not SafeFirst.Contains(first) Then Return False
                ' Safe names with options that write or run something.
                Select Case first.ToLowerInvariant()
                    Case "sort", "tree" : If args.Any(Function(t) t.StartsWith("-o", StringComparison.Ordinal)) Then Return False
                    Case "uniq" : If positional.Count >= 2 Then Return False
                    Case "rg" : If HasFlag(args,"--pre") Then Return False
                    Case "date" : If HasFlag(args,"-s", "--set") OrElse positional.Any(Function(t) Not t.StartsWith("+") AndAlso Not t.StartsWith("""+") AndAlso Not t.StartsWith("'+")) Then Return False
                    Case "hostname" : If positional.Count > 0 Then Return False
                End Select
            Next
            Return True
        End Function

        ''' <summary>The file or folder a tool works on (Nothing when it has none).</summary>
        Public Function TargetPath(toolName As String, input As JsonElement) As String
            Dim raw As String = Nothing
            Select Case toolName
                Case "Read", "Write", "Edit", "MultiEdit" : raw = Jx.StrOf(input, "file_path", Nothing)
                Case "NotebookEdit" : raw = Jx.StrOf(input, "notebook_path", Nothing)
                Case "LS", "Glob", "Grep" : raw = Jx.StrOf(input, "path", ".")
                    If toolName = "Glob" AndAlso Path.IsPathRooted(Jx.StrOf(input, "pattern", "")) Then raw = Jx.StrOf(input, "pattern", "")
            End Select
            If String.IsNullOrWhiteSpace(raw) Then Return Nothing
            Try
                Dim firstWild = raw.IndexOfAny({"*"c, "?"c, "{"c, "["c})
                If firstWild >= 0 Then raw = raw.Substring(0, Math.Max(0, raw.LastIndexOfAny({"\"c, "/"c}, firstWild)))
                Return _state.Resolve(If(raw.Length = 0, ".", raw))
            Catch ex As Exception When TypeOf ex Is ArgumentException OrElse TypeOf ex Is NotSupportedException OrElse TypeOf ex Is PathTooLongException
                Return Nothing
            End Try
        End Function

        ''' <summary>Gitignore-style path patterns: "src/**", "*.env" (anywhere), "~/x/**", "//C:/abs/**" or "C:\abs\**".</summary>
        Public Function PathMatches(spec As String, fullPath As String) As Boolean
            Dim s = spec.Replace("\"c, "/"c)
            Dim baseDir As String
            If s.StartsWith("//") Then
                s = s.Substring(2)
                If Regex.IsMatch(s, "^[A-Za-z]/") Then s = s.Substring(0, 1) & ":" & s.Substring(1)
                baseDir = Nothing
            ElseIf s.StartsWith("~/") Then
                s = Paths.Slashes(Paths.Home()).TrimEnd("/"c) & "/" & s.Substring(2)
                baseDir = Nothing
            ElseIf Regex.IsMatch(s, "^[A-Za-z]:/") Then
                baseDir = Nothing
            Else
                baseDir = _state.ProjectDir
                s = s.TrimStart("/"c)
                If Not s.Contains("/"c) Then s = "**/" & s
                s = Paths.Slashes(baseDir).TrimEnd("/"c) & "/" & s
            End If
            Dim rx As New Regex("^" & FileWalker.Convert(s) & "(?:/.*)?$", RegexOptions.IgnoreCase)
            Return rx.IsMatch(Paths.Slashes(fullPath))
        End Function

        Public Shared Function HostOf(url As String) As String
            Dim u As Uri = Nothing
            If Uri.TryCreate(If(url, ""), UriKind.Absolute, u) Then Return u.Host
            Return url
        End Function

        ''' <summary>The rule offered by "Yes, and don't ask again": Bash(git commit:*), Edit(src/**), WebFetch(domain:x), mcp__server__tool.</summary>
        Public Function SuggestRule(toolName As String, input As JsonElement) As String
            Select Case toolName
                Case "Bash", "Monitor"
                    Dim c = NormalizeCommand(Jx.StrOf(input, "command", ""))
                    Dim parts = SplitCommand(c)
                    If parts.Count <> 1 Then Return Nothing
                    Dim tokens = c.Split(" "c)
                    Dim prefix = tokens(0)
                    If tokens.Length > 1 AndAlso Regex.IsMatch(tokens(1), "^[a-zA-Z][\w:-]*$") Then prefix &= " " & tokens(1)
                    Return $"Bash({prefix}:*)"
                Case "WebFetch"
                    Return $"WebFetch(domain:{HostOf(Jx.StrOf(input, "url", ""))})"
                Case "Read", "Glob", "Grep", "LS"
                    Dim t = TargetPath(toolName, input)
                    If t Is Nothing Then Return Nothing
                    Dim folder = If(Directory.Exists(t), t, Path.GetDirectoryName(t))
                    Return "Read(//" & Paths.Slashes(folder).Replace(":", "") & "/**)"
                Case Else
                    If toolName.StartsWith("mcp__") Then Return toolName
                    Return toolName
            End Select
        End Function

    End Class

End Namespace
