Option Strict On
Option Explicit On
Option Infer On

' Well-known MCP servers: "agentic mcp catalog" lists them, "agentic mcp add <name> [folder]" adds one by name. The same
' list is in the Agentic desktop app (Local MCP) and on the Agentic server's MCP page.

Imports System.Text.Json
Imports System.Text.Json.Nodes

Namespace Mcp

    Friend NotInheritable Class McpCatalogEntry
        Public Property Id As String
        Public Property Name As String
        Public Property Category As String
        Public Property Needs As String
        Public Property Blurb As String
        Public Property Command As String
        Public Property Args As New List(Of String)
        Public Property Url As String
        ''' <summary>The environment variable whose value is sent as "Authorization: Bearer …" (hosted servers).</summary>
        Public Property Bearer As String
        Public Property Approval As Boolean
        ''' <summary>Environment variables the server needs (an API key), with where to get them.</summary>
        Public Property Env As New List(Of (Key As String, Hint As String))
        Public Property Docs As String

        Public ReadOnly Property NeedsFolder As Boolean
            Get
                Return Args.Contains("{folder}")
            End Get
        End Property
    End Class

    Friend NotInheritable Class McpCatalog

        Private Sub New()
        End Sub

        Private Const Data As String = "[{""id"":""filesystem"",""name"":""Filesystem"",""category"":""Files & code"",""needs"":""Node.js"",""blurb"":""Read, search and edit files in the folders you allow."",""command"":""npx"",""args"":[""-y"",""@modelcontextprotocol/server-filesystem"",""{folder}""],""approval"":true,""docs"":""https://github.com/modelcontextprotocol/servers/tree/main/src/filesystem""},{""id"":""git"",""name"":""Git"",""category"":""Files & code"",""needs"":""uv (Python)"",""blurb"":""Read a Git repository: status, diffs, log, branches and commits."",""command"":""uvx"",""args"":[""mcp-server-git"",""--repository"",""{folder}""],""approval"":true,""docs"":""https://github.com/modelcontextprotocol/servers/tree/main/src/git""},{""id"":""github"",""name"":""GitHub"",""category"":""Files & code"",""needs"":""a GitHub token"",""blurb"":""GitHub's official server: repositories, issues, pull requests, code search and Actions."",""url"":""https://api.githubcopilot.com/mcp/"",""bearer"":""GITHUB_TOKEN"",""approval"":true,""env"":[{""key"":""GITHUB_TOKEN"",""hint"":""a personal access token (github.com/settings/tokens)""}],""docs"":""https://github.com/github/github-mcp-server""},{""id"":""desktop-commander"",""name"":""Desktop Commander"",""category"":""Files & code"",""needs"":""Node.js"",""blurb"":""Terminal commands, processes and file editing on the computer it runs on."",""command"":""npx"",""args"":[""-y"",""@wonderwhy-er/desktop-commander""],""approval"":true,""docs"":""https://github.com/wonderwhy-er/DesktopCommanderMCP""},{""id"":""playwright"",""name"":""Playwright browser"",""category"":""Browser & web"",""needs"":""Node.js 20+"",""blurb"":""Microsoft's browser automation: open pages, click, type, fill forms and read pages through the accessibility tree."",""command"":""npx"",""args"":[""-y"",""@playwright/mcp@latest""],""approval"":true,""docs"":""https://github.com/microsoft/playwright-mcp""},{""id"":""chrome-devtools"",""name"":""Chrome DevTools"",""category"":""Browser & web"",""needs"":""Node.js 20.19+ and Chrome"",""blurb"":""Google's server for Chrome: drive the browser, read the console and network, record performance traces."",""command"":""npx"",""args"":[""-y"",""chrome-devtools-mcp@latest""],""approval"":true,""docs"":""https://github.com/ChromeDevTools/chrome-devtools-mcp""},{""id"":""fetch"",""name"":""Fetch"",""category"":""Browser & web"",""needs"":""uv (Python)"",""blurb"":""Download a web page and turn it into Markdown for the model."",""command"":""uvx"",""args"":[""mcp-server-fetch""],""docs"":""https://github.com/modelcontextprotocol/servers/tree/main/src/fetch""},{""id"":""firecrawl"",""name"":""Firecrawl"",""category"":""Browser & web"",""needs"":""Node.js 22+ and a Firecrawl API key"",""blurb"":""Scrape, crawl and extract structured data from websites, including JavaScript-heavy ones."",""command"":""npx"",""args"":[""-y"",""firecrawl-mcp""],""env"":[{""key"":""FIRECRAWL_API_KEY"",""hint"":""from firecrawl.dev""}],""docs"":""https://github.com/mendableai/firecrawl-mcp-server""},{""id"":""brave-search"",""name"":""Brave Search"",""category"":""Search"",""needs"":""Node.js and a Brave Search API key"",""blurb"":""Web, news, image and local search through Brave's independent index."",""command"":""npx"",""args"":[""-y"",""@brave/brave-search-mcp-server""],""env"":[{""key"":""BRAVE_API_KEY"",""hint"":""from brave.com/search/api""}],""docs"":""https://github.com/brave/brave-search-mcp-server""},{""id"":""tavily"",""name"":""Tavily"",""category"":""Search"",""needs"":""Node.js and a Tavily API key"",""blurb"":""Search built for AI agents, with page extraction and site crawling."",""command"":""npx"",""args"":[""-y"",""tavily-mcp@latest""],""env"":[{""key"":""TAVILY_API_KEY"",""hint"":""from tavily.com""}],""docs"":""https://github.com/tavily-ai/tavily-mcp""},{""id"":""exa"",""name"":""Exa"",""category"":""Search"",""needs"":""Node.js 20+ and an Exa API key"",""blurb"":""Neural web search: find pages by meaning, research papers, companies and code."",""command"":""npx"",""args"":[""-y"",""exa-mcp-server""],""env"":[{""key"":""EXA_API_KEY"",""hint"":""from dashboard.exa.ai""}],""docs"":""https://github.com/exa-labs/exa-mcp-server""},{""id"":""context7"",""name"":""Context7"",""category"":""Documentation"",""needs"":""Node.js 20+"",""blurb"":""Up-to-date documentation and code examples for thousands of libraries, so code uses current APIs."",""command"":""npx"",""args"":[""-y"",""@upstash/context7-mcp""],""docs"":""https://github.com/upstash/context7""},{""id"":""microsoft-learn"",""name"":""Microsoft Learn"",""category"":""Documentation"",""needs"":""nothing (hosted)"",""blurb"":""Search and read official Microsoft documentation: .NET, Azure, Windows, Microsoft 365."",""url"":""https://learn.microsoft.com/api/mcp"",""docs"":""https://github.com/MicrosoftDocs/mcp""},{""id"":""deepwiki"",""name"":""DeepWiki"",""category"":""Documentation"",""needs"":""nothing (hosted)"",""blurb"":""Ask questions about any public GitHub repository and read its generated wiki."",""url"":""https://mcp.deepwiki.com/mcp"",""docs"":""https://docs.devin.ai/work-with-devin/deepwiki-mcp""},{""id"":""aws-docs"",""name"":""AWS Documentation"",""category"":""Documentation"",""needs"":""uv (Python)"",""blurb"":""Search and read AWS documentation and get recommendations for related pages."",""command"":""uvx"",""args"":[""awslabs.aws-documentation-mcp-server@latest""],""docs"":""https://github.com/awslabs/mcp""},{""id"":""notion"",""name"":""Notion"",""category"":""Productivity"",""needs"":""Node.js and a Notion integration token"",""blurb"":""Search, read and update pages and databases in your Notion workspace."",""command"":""npx"",""args"":[""-y"",""@notionhq/notion-mcp-server""],""approval"":true,""env"":[{""key"":""NOTION_TOKEN"",""hint"":""an internal integration token (notion.so/my-integrations)""}],""docs"":""https://github.com/makenotion/notion-mcp-server""},{""id"":""supabase"",""name"":""Supabase"",""category"":""Data"",""needs"":""Node.js and a Supabase access token"",""blurb"":""Query and manage Supabase projects: tables, SQL, migrations, logs (read-only by default here)."",""command"":""npx"",""args"":[""-y"",""@supabase/mcp-server-supabase@latest"",""--read-only""],""approval"":true,""env"":[{""key"":""SUPABASE_ACCESS_TOKEN"",""hint"":""a personal access token (supabase.com/dashboard/account/tokens)""}],""docs"":""https://github.com/supabase-community/supabase-mcp""},{""id"":""memory"",""name"":""Memory"",""category"":""Thinking"",""needs"":""Node.js"",""blurb"":""A knowledge graph the model can store facts in and recall across sessions."",""command"":""npx"",""args"":[""-y"",""@modelcontextprotocol/server-memory""],""docs"":""https://github.com/modelcontextprotocol/servers/tree/main/src/memory""},{""id"":""sequential-thinking"",""name"":""Sequential Thinking"",""category"":""Thinking"",""needs"":""Node.js"",""blurb"":""Structured step-by-step reasoning the model can revise and branch for hard problems."",""command"":""npx"",""args"":[""-y"",""@modelcontextprotocol/server-sequential-thinking""],""docs"":""https://github.com/modelcontextprotocol/servers/tree/main/src/sequentialthinking""},{""id"":""time"",""name"":""Time"",""category"":""Thinking"",""needs"":""uv (Python)"",""blurb"":""The current time in any time zone, and conversions between zones."",""command"":""uvx"",""args"":[""mcp-server-time""],""docs"":""https://github.com/modelcontextprotocol/servers/tree/main/src/time""},{""id"":""rea"",""name"":""REA — reverse engineering"",""category"":""Security"",""needs"":""Node.js 22+; optional Ghidra, Hopper or IDA"",""blurb"":""Reverse-engineer native binaries, .NET assemblies, JS/Electron apps, Android APKs and websites: pseudocode, disassembly, modules and behaviour."",""command"":""npx"",""args"":[""-y"",""rea-agents@6.3.0"",""mcp""],""approval"":true,""seed"":true,""docs"":""https://github.com/morluto/rea""}]"

        Private Shared _all As List(Of McpCatalogEntry)

        Public Shared ReadOnly Property All As List(Of McpCatalogEntry)
            Get
                If _all Is Nothing Then _all = Parse(Data)
                Return _all
            End Get
        End Property

        Public Shared Function Find(id As String) As McpCatalogEntry
            Return All.FirstOrDefault(Function(e) String.Equals(e.Id, If(id, "").Trim(), StringComparison.OrdinalIgnoreCase))
        End Function

        Private Shared Function Parse(json As String) As List(Of McpCatalogEntry)
            Dim list As New List(Of McpCatalogEntry)
            For Each n In JsonNode.Parse(json).AsArray()
                Dim e As New McpCatalogEntry With {
                    .Id = n("id")?.ToString(), .Name = n("name")?.ToString(), .Category = n("category")?.ToString(), .Needs = n("needs")?.ToString(),
                    .Blurb = n("blurb")?.ToString(), .Command = n("command")?.ToString(), .Url = n("url")?.ToString(), .Bearer = n("bearer")?.ToString(),
                    .Approval = n("approval") IsNot Nothing AndAlso n("approval").GetValue(Of Boolean)(), .Docs = n("docs")?.ToString()}
                For Each a In If(n("args")?.AsArray(), New JsonArray())
                    e.Args.Add(a.ToString())
                Next
                For Each v In If(n("env")?.AsArray(), New JsonArray())
                    e.Env.Add((v("key").ToString(), v("hint")?.ToString()))
                Next
                list.Add(e)
            Next
            Return list
        End Function

        ''' <summary>
        ''' The server settings for an entry. {folder} becomes folder; a key not given in env is read from the environment
        ''' variable of the same name when the server starts (%NAME%).
        ''' </summary>
        Public Shared Function ToServer(e As McpCatalogEntry, folder As String, env As Dictionary(Of String, String)) As McpServerOptions
            Dim s As New McpServerOptions With {.Id = e.Id, .Name = e.Name, .RequireApproval = e.Approval}
            Dim valueOf = Function(key As String) If(env IsNot Nothing AndAlso env.ContainsKey(key), env(key), "%" & key & "%")
            If e.Url IsNot Nothing Then
                s.Transport = McpTransportKind.Http
                s.Endpoint = e.Url
                If e.Bearer IsNot Nothing Then s.Headers("Authorization") = "Bearer " & valueOf(e.Bearer)
            Else
                s.Transport = McpTransportKind.Stdio
                s.Command = e.Command
                s.Args = e.Args.Select(Function(a) If(a = "{folder}", folder, a)).ToList()
            End If
            For Each k In e.Env.Where(Function(x) x.Key <> e.Bearer)
                s.Env(k.Key) = valueOf(k.Key)
            Next
            If env IsNot Nothing Then
                For Each kv In env.Where(Function(x) x.Key <> e.Bearer)
                    s.Env(kv.Key) = kv.Value
                Next
            End If
            Return s
        End Function

        ''' <summary>The catalog as lines for a terminal, grouped by kind; installed marks the servers already added.</summary>
        Public Shared Function Describe(installed As ISet(Of String)) As List(Of String)
            Dim lines As New List(Of String)
            For Each g In All.GroupBy(Function(e) e.Category)
                lines.Add(g.Key)
                For Each e In g
                    Dim mark = If(installed IsNot Nothing AndAlso installed.Contains(e.Id), " (added)", "")
                    lines.Add($"  {e.Id,-20} {e.Name}{mark} — {e.Blurb}")
                    lines.Add($"  {"",-20} needs {e.Needs}{If(e.Env.Count > 0, " · set " & String.Join(", ", e.Env.Select(Function(x) x.Key)), "")}")
                Next
            Next
            Return lines
        End Function
    End Class

End Namespace
