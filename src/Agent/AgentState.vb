Option Strict On
Option Explicit On
Option Infer On

Imports System.Collections.Concurrent
Imports System.IO
Imports AgenticCli.Core

Namespace Agent

    ''' <summary>How much the agent may do without asking (Shift+Tab cycles default → acceptEdits → plan).</summary>
    Friend Enum PermissionMode
        [Default]
        AcceptEdits
        Plan
        BypassPermissions
    End Enum

    Friend NotInheritable Class TodoItem
        Public Property Content As String
        ''' <summary>pending | in_progress | completed</summary>
        Public Property Status As String = "pending"
        Public Property ActiveForm As String
    End Class

    ''' <summary>What the tools and the turn runner share during a session.</summary>
    Friend NotInheritable Class AgentState

        Public Property ProjectDir As String
        ''' <summary>Where relative paths start and where Bash runs (cd in Bash moves it).</summary>
        Public Property Cwd As String
        Public ReadOnly Property ExtraDirs As New List(Of String)
        Public Property Mode As PermissionMode = PermissionMode.Default
        Public Property Settings As Settings
        Public Property Project As ProjectInfo
        Public Property Skills As Skills.SkillStore
        Public Property Mcp As Mcp.McpHost
        Public Property Hooks As HookRunner
        Public Property Shells As Tools.ShellManager
        ''' <summary>Commands the Monitor tool watches (their output arrives as messages while the session waits).</summary>
        Public Property Monitors As Tools.MonitorManager
        ''' <summary>/ide: the editor this session is connected to (VS Code), or Nothing.</summary>
        Public Property Ide As Core.IdeLink
        Public Property Checkpoints As CheckpointStore
        Public Property ChatId As String
        Public Property Turn As Integer
        Public Property ShowThinking As Boolean
        Public Property Verbose As Boolean
        Public Property Todos As New List(Of TodoItem)
        ''' <summary>Files read in this session (full path → last write time then), so edits can check they are current.</summary>
        Public ReadOnly Property ReadFiles As New ConcurrentDictionary(Of String, DateTime)(StringComparer.OrdinalIgnoreCase)
        ''' <summary>The size of each file when it was read: a change within the same timestamp tick still shows.</summary>
        Public ReadOnly Property ReadSizes As New ConcurrentDictionary(Of String, Long)(StringComparer.OrdinalIgnoreCase)
        ''' <summary>Print mode: nothing can be asked; unanswered permission questions are refused.</summary>
        Public Property Interactive As Boolean = True
        ''' <summary>
        ''' Print mode driven by another program (stream-json input): permission questions go to it.
        ''' Arguments: tool name, input, title, reason. Returns allow, a message for the model, and whether to stop asking.
        ''' </summary>
        Public Property PermissionAsker As Func(Of String, Text.Json.JsonElement, String, String, (Allowed As Boolean, Message As String, Remember As Boolean))
        ''' <summary>A message to send by itself after the current answer (e.g. "implement the approved plan").</summary>
        Public Property PendingFollowUp As String

        Public Function WorkingDirs() As List(Of String)
            Dim list As New List(Of String) From {ProjectDir}
            If Cwd IsNot Nothing AndAlso Not Paths.IsInside(Cwd, ProjectDir) Then list.Add(Cwd)
            For Each d In ExtraDirs
                If Not list.Any(Function(x) Paths.IsInside(d, x)) Then list.Add(d)
            Next
            For Each d In Settings.Strings("permissions.additionalDirectories")
                Dim full = Paths.Full(d, ProjectDir)
                If Not list.Any(Function(x) Paths.IsInside(full, x)) Then list.Add(full)
            Next
            Return list
        End Function

        Public Function InWorkingDirs(p As String) As Boolean
            If String.IsNullOrEmpty(p) Then Return True
            Return WorkingDirs().Any(Function(d) Paths.IsInside(p, d))
        End Function

        ''' <summary>A tool's path argument as a full path (relative paths start at the working directory).</summary>
        Public Function Resolve(p As String) As String
            Return Paths.Full(p, If(Cwd, ProjectDir))
        End Function

        Public Function Display(p As String) As String
            Return Paths.Display(p, If(Cwd, ProjectDir))
        End Function

        Public Sub MarkRead(fullPath As String)
            Try
                ReadFiles(fullPath) = File.GetLastWriteTimeUtc(fullPath)
                ReadSizes(fullPath) = New FileInfo(fullPath).Length
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
            End Try
        End Sub

        Public ReadOnly Property ModeLabel As String
            Get
                Select Case Mode
                    Case PermissionMode.AcceptEdits : Return "acceptEdits"
                    Case PermissionMode.Plan : Return "plan"
                    Case PermissionMode.BypassPermissions : Return "bypassPermissions"
                    Case Else : Return "default"
                End Select
            End Get
        End Property

        Public Shared Function ParseMode(s As String, fallback As PermissionMode) As PermissionMode
            Select Case If(s, "").Trim().ToLowerInvariant().Replace("_", "").Replace("-", "").Replace(" ", "")
                Case "default", "normal", "ask", "manual" : Return PermissionMode.Default
                Case "acceptedits", "accept", "autoedit", "edits", "auto" : Return PermissionMode.AcceptEdits
                Case "plan", "planmode", "readonly" : Return PermissionMode.Plan
                Case "bypasspermissions", "bypass", "yolo", "dangerouslyskippermissions" : Return PermissionMode.BypassPermissions
                Case Else : Return fallback
            End Select
        End Function

    End Class

End Namespace
