Option Strict On
Option Explicit On
Option Infer On

Imports System.IO
Imports System.Text.Json
Imports AgenticCli.Core

Namespace Agent

    Friend NotInheritable Class CheckpointFile
        Public Property Path As String
        Public Property Existed As Boolean
        Public Property Backup As String
    End Class

    ''' <summary>One message you sent: the files the agent changed while answering it, as they were before.</summary>
    Friend NotInheritable Class Checkpoint
        Public Property Turn As Integer
        Public Property Prompt As String
        Public Property MessageId As String
        Public Property TimeUtc As DateTime
        Public Property Files As New List(Of CheckpointFile)
    End Class

    ''' <summary>
    ''' Checkpoints like Claude Code's /rewind and Cline's: before a tool changes a file for the first time in a turn, its
    ''' content is saved; going back restores every file changed since that turn (and can also cut the conversation there).
    ''' Changes made by shell commands are not tracked.
    ''' </summary>
    Friend NotInheritable Class CheckpointStore

        Private ReadOnly _dir As String
        Private ReadOnly _list As New List(Of Checkpoint)
        Private ReadOnly _lock As New Object()

        Public Sub New(dir As String)
            _dir = Paths.EnsureDir(dir)
            Load()
        End Sub

        Private ReadOnly Property ManifestFile As String
            Get
                Return Path.Combine(_dir, "checkpoints.json")
            End Get
        End Property

        Private Sub Load()
            Dim root As JsonElement
            If Not Jx.TryParse(Paths.ReadText(ManifestFile), root) OrElse root.ValueKind <> JsonValueKind.Array Then Return
            For Each c In root.EnumerateArray()
                Dim cp As New Checkpoint With {.Turn = Jx.IntOf(c, "turn"), .Prompt = Jx.StrOf(c, "prompt"), .MessageId = Jx.StrOf(c, "messageId", Nothing),
                                               .TimeUtc = If(Jx.DateOf(c, "timeUtc"), DateTime.UtcNow)}
                For Each f In Jx.ArrOf(c, "files")
                    cp.Files.Add(New CheckpointFile With {.Path = Jx.StrOf(f, "path"), .Existed = Jx.BoolOf(f, "existed"), .Backup = Jx.StrOf(f, "backup", Nothing)})
                Next
                _list.Add(cp)
            Next
        End Sub

        Private Sub Save()
            Try
                Paths.WriteText(ManifestFile, Jx.Stringify(_list.Select(Function(c) New With {.turn = c.Turn, .prompt = c.Prompt, .messageId = c.MessageId, .timeUtc = c.TimeUtc,
                                                                                               .files = c.Files.Select(Function(f) New With {.path = f.Path, .existed = f.Existed, .backup = f.Backup}).ToList()}).ToList()))
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
            End Try
        End Sub

        Public ReadOnly Property NextTurn As Integer
            Get
                SyncLock _lock
                    Return If(_list.Count = 0, 1, _list.Max(Function(c) c.Turn) + 1)
                End SyncLock
            End Get
        End Property

        Public Sub BeginTurn(turn As Integer, prompt As String)
            SyncLock _lock
                _list.Add(New Checkpoint With {.Turn = turn, .Prompt = prompt, .TimeUtc = DateTime.UtcNow})
                Save()
            End SyncLock
        End Sub

        Public Sub SetMessageId(turn As Integer, messageId As String)
            SyncLock _lock
                Dim cp = _list.LastOrDefault(Function(c) c.Turn = turn)
                If cp Is Nothing Then Return
                cp.MessageId = messageId
                Save()
            End SyncLock
        End Sub

        ''' <summary>Called before a tool changes a file: keeps its current content (once per turn).</summary>
        Public Sub Snapshot(file As String)
            SyncLock _lock
                Dim cp = _list.LastOrDefault()
                If cp Is Nothing Then Return
                If cp.Files.Any(Function(f) f.Path.Equals(file, StringComparison.OrdinalIgnoreCase)) Then Return
                Dim entry As New CheckpointFile With {.Path = file, .Existed = IO.File.Exists(file)}
                If entry.Existed Then
                    Try
                        entry.Backup = Path.Combine(_dir, $"t{cp.Turn}-{cp.Files.Count}-{Path.GetFileName(file)}.bak")
                        IO.File.Copy(file, entry.Backup, True)
                    Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
                        Return
                    End Try
                End If
                cp.Files.Add(entry)
                Save()
            End SyncLock
        End Sub

        Public Function Turns() As List(Of Checkpoint)
            SyncLock _lock
                Return _list.ToList()
            End SyncLock
        End Function

        ''' <summary>Puts back every file changed in this turn and the later ones (newest first). Returns the files restored.</summary>
        Public Function RestoreFrom(turn As Integer) As List(Of String)
            Dim restored As New List(Of String)
            SyncLock _lock
                For Each cp In _list.Where(Function(c) c.Turn >= turn).OrderByDescending(Function(c) c.Turn)
                    For Each f In cp.Files
                        Try
                            If f.Existed AndAlso f.Backup IsNot Nothing AndAlso IO.File.Exists(f.Backup) Then
                                Directory.CreateDirectory(Path.GetDirectoryName(f.Path))
                                IO.File.Copy(f.Backup, f.Path, True)
                            ElseIf Not f.Existed AndAlso IO.File.Exists(f.Path) Then
                                IO.File.Delete(f.Path)
                            End If
                            If Not restored.Contains(f.Path, StringComparer.OrdinalIgnoreCase) Then restored.Add(f.Path)
                        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
                        End Try
                    Next
                Next
            End SyncLock
            Return restored
        End Function

        Public Sub DropFrom(turn As Integer)
            SyncLock _lock
                _list.RemoveAll(Function(c) c.Turn >= turn)
                Save()
            End SyncLock
        End Sub

        ''' <summary>Files changed in the turns from this one on (what a code rewind would restore).</summary>
        Public Function FilesFrom(turn As Integer) As List(Of String)
            SyncLock _lock
                Return _list.Where(Function(c) c.Turn >= turn).SelectMany(Function(c) c.Files.Select(Function(f) f.Path)).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            End SyncLock
        End Function

    End Class

End Namespace
