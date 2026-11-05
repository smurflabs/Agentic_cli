Option Strict On
Option Explicit On
Option Infer On

Imports System.IO
Imports System.Security.Cryptography
Imports System.Text
Imports System.Text.Json
Imports System.Text.Json.Nodes

Namespace Core

    ''' <summary>
    ''' The server address and API key, from config.json next to agentic.exe:
    '''   { "serverBaseUrl": "https://agentic.smurflabs.xyz", "apiKey": "agk_…" }
    ''' AGENTIC_SERVER and AGENTIC_API_KEY override it (CI, scripts). Keys saved by older versions in
    ''' %APPDATA%\AgenticCli\secrets.bin (DPAPI) are still read when config.json has none.
    ''' </summary>
    Friend NotInheritable Class Credentials

        Public Const DefaultServer As String = "https://agentic.smurflabs.xyz"

        Public Property ServerUrl As String = ""
        Public Property ApiKey As String = ""
        ''' <summary>True when the values come from environment variables (nothing to save).</summary>
        Public Property FromEnvironment As Boolean
        ''' <summary>Where the key came from: config.json, secrets.bin or environment.</summary>
        Public Property Source As String = ""

        ''' <summary>config.json in the folder agentic (agentic.exe) runs from.</summary>
        Public Shared Function ConfigFile() As String
            Return Path.Combine(AppContext.BaseDirectory, "config.json")
        End Function

        Public Shared Function Load() As Credentials
            Dim c As New Credentials()
            Dim cfg = ReadConfig()
            If cfg IsNot Nothing Then
                c.ServerUrl = If(Str(cfg, "serverBaseUrl"), If(Str(cfg, "serverUrl"), If(Str(cfg, "baseUrl"), "")))
                c.ApiKey = If(Str(cfg, "apiKey"), "")
                If c.ApiKey.Length > 0 Then c.Source = "config.json"
            End If
            If c.ApiKey.Length = 0 Then
                Try
                    Dim file = Paths.CredentialsFile()
                    If OperatingSystem.IsWindows() AndAlso IO.File.Exists(file) Then
                        Dim bytes = ProtectedData.Unprotect(IO.File.ReadAllBytes(file), Nothing, DataProtectionScope.CurrentUser)
                        Dim root = Jx.Parse(Encoding.UTF8.GetString(bytes))
                        If c.ServerUrl.Length = 0 Then c.ServerUrl = Jx.StrOf(root, "serverUrl")
                        c.ApiKey = Jx.StrOf(root, "apiKey")
                        If c.ApiKey.Length > 0 Then c.Source = "secrets.bin"
                    End If
                Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is CryptographicException OrElse TypeOf ex Is JsonException OrElse TypeOf ex Is UnauthorizedAccessException
                End Try
            End If
            Dim envServer = Environment.GetEnvironmentVariable("AGENTIC_SERVER")
            Dim envKey = Environment.GetEnvironmentVariable("AGENTIC_API_KEY")
            If Not String.IsNullOrWhiteSpace(envServer) Then c.ServerUrl = envServer.Trim() : c.FromEnvironment = True
            If Not String.IsNullOrWhiteSpace(envKey) Then c.ApiKey = envKey.Trim() : c.FromEnvironment = True : c.Source = "environment"
            c.ServerUrl = c.ServerUrl.Trim().TrimEnd("/"c)
            If String.IsNullOrWhiteSpace(c.ServerUrl) Then c.ServerUrl = DefaultServer
            Return c
        End Function

        ''' <summary>Writes serverBaseUrl and apiKey to config.json (other settings in it are kept). Returns the file written.</summary>
        Public Function Save() As String
            Dim file = ConfigFile()
            Dim cfg = If(ReadConfig(), New JsonObject())
            cfg("serverBaseUrl") = ServerUrl
            cfg("apiKey") = ApiKey
            WriteConfig(cfg)
            Source = "config.json"
            ' The key now lives in config.json; drop the copy older versions kept.
            DeleteLegacy()
            Return file
        End Function

        ''' <summary>Removes the API key (config.json keeps the server address).</summary>
        Public Shared Sub Forget()
            Dim cfg = ReadConfig()
            If cfg IsNot Nothing AndAlso cfg.ContainsKey("apiKey") Then
                cfg("apiKey") = ""
                Try
                    WriteConfig(cfg)
                Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
                End Try
            End If
            DeleteLegacy()
        End Sub

        Private Shared Sub WriteConfig(cfg As JsonObject)
            Dim json = cfg.ToJsonString(New JsonSerializerOptions With {.WriteIndented = True}) & Environment.NewLine
            IO.File.WriteAllText(ConfigFile(), json, New UTF8Encoding(False))
        End Sub

        Private Shared Sub DeleteLegacy()
            Try
                Dim file = Paths.CredentialsFile()
                If IO.File.Exists(file) Then IO.File.Delete(file)
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
            End Try
        End Sub

        Private Shared Function ReadConfig() As JsonObject
            Try
                Dim file = ConfigFile()
                If Not IO.File.Exists(file) Then Return Nothing
                Dim options As New JsonDocumentOptions With {.CommentHandling = JsonCommentHandling.Skip, .AllowTrailingCommas = True}
                Return TryCast(JsonNode.Parse(IO.File.ReadAllText(file), Nothing, options), JsonObject)
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is JsonException OrElse TypeOf ex Is UnauthorizedAccessException
                Return Nothing
            End Try
        End Function

        Private Shared Function Str(o As JsonObject, name As String) As String
            Dim v = TryCast(o(name), JsonValue)
            Dim s As String = Nothing
            If v Is Nothing OrElse Not v.TryGetValue(s) OrElse String.IsNullOrWhiteSpace(s) Then Return Nothing
            Return s.Trim()
        End Function

        Public Function IsConfigured() As Boolean
            Return ServerUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase) AndAlso ApiKey.Length > 0
        End Function

        ''' <summary>The key with all but its prefix and last four characters hidden.</summary>
        Public Function MaskedKey() As String
            If ApiKey.Length <= 10 Then Return New String("*"c, ApiKey.Length)
            Return ApiKey.Substring(0, 4) & New String("*"c, 8) & ApiKey.Substring(ApiKey.Length - 4)
        End Function

    End Class

End Namespace
