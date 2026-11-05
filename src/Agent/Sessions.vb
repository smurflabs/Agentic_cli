Option Strict On
Option Explicit On
Option Infer On

Imports System.IO
Imports System.Text.Json
Imports AgenticCli.Core

Namespace Agent

    Friend NotInheritable Class SessionRecord
        Public Property ChatId As String
        Public Property Title As String
        Public Property Model As String
        Public Property CreatedUtc As DateTime
        Public Property UpdatedUtc As DateTime
        Public Property Turns As Integer
        Public Property LastPrompt As String
    End Class

    ''' <summary>The conversations started from a project folder (for --continue and /resume), and its input history.</summary>
    Friend NotInheritable Class Sessions

        Private Shared Function IndexFile(projectDir As String) As String
            Return Path.Combine(Paths.ProjectDataDir(projectDir), "sessions.json")
        End Function

        Public Shared Function List(projectDir As String) As List(Of SessionRecord)
            Dim result As New List(Of SessionRecord)
            Dim root As JsonElement
            If Not Jx.TryParse(Paths.ReadText(IndexFile(projectDir)), root) OrElse root.ValueKind <> JsonValueKind.Array Then Return result
            For Each s In root.EnumerateArray()
                result.Add(New SessionRecord With {.ChatId = Jx.StrOf(s, "chatId"), .Title = Jx.StrOf(s, "title"), .Model = Jx.StrOf(s, "model"),
                                                   .CreatedUtc = If(Jx.DateOf(s, "createdUtc"), DateTime.UtcNow), .UpdatedUtc = If(Jx.DateOf(s, "updatedUtc"), DateTime.UtcNow),
                                                   .Turns = Jx.IntOf(s, "turns"), .LastPrompt = Jx.StrOf(s, "lastPrompt")})
            Next
            Return result.Where(Function(r) r.ChatId.Length > 0).OrderByDescending(Function(r) r.UpdatedUtc).ToList()
        End Function

        Public Shared Sub Upsert(projectDir As String, record As SessionRecord)
            Dim all = List(projectDir)
            Dim existing = all.FirstOrDefault(Function(r) r.ChatId = record.ChatId)
            If existing IsNot Nothing Then
                If String.IsNullOrWhiteSpace(record.Title) Then record.Title = existing.Title
                record.CreatedUtc = existing.CreatedUtc
                all.Remove(existing)
            End If
            all.Insert(0, record)
            Try
                Paths.WriteText(IndexFile(projectDir), Jx.Stringify(all.Take(200).Select(Function(r) New With {
                    .chatId = r.ChatId, .title = r.Title, .model = r.Model, .createdUtc = r.CreatedUtc, .updatedUtc = r.UpdatedUtc, .turns = r.Turns,
                    .lastPrompt = If(r.LastPrompt Is Nothing, Nothing, If(r.LastPrompt.Length > 200, r.LastPrompt.Substring(0, 200), r.LastPrompt))}).ToList(), True))
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
            End Try
        End Sub

        Public Shared Sub Remove(projectDir As String, chatId As String)
            Dim all = List(projectDir)
            If all.RemoveAll(Function(r) r.ChatId = chatId) = 0 Then Return
            Paths.WriteText(IndexFile(projectDir), Jx.Stringify(all.Select(Function(r) New With {.chatId = r.ChatId, .title = r.Title, .model = r.Model, .createdUtc = r.CreatedUtc,
                                                                                                    .updatedUtc = r.UpdatedUtc, .turns = r.Turns, .lastPrompt = r.LastPrompt}).ToList(), True))
        End Sub

        ' ------------------------------------------------------------------ input history (↑ and Ctrl+R in the message box)

        Private Shared Function HistoryFile(projectDir As String) As String
            Return Path.Combine(Paths.ProjectDataDir(projectDir), "history.jsonl")
        End Function

        Public Shared Function History(projectDir As String) As List(Of String)
            Dim list As New List(Of String)
            Dim text = Paths.ReadText(HistoryFile(projectDir))
            If text Is Nothing Then Return list
            For Each line In text.Split(ChrW(10))
                If line.Trim().Length = 0 Then Continue For
                Dim e As JsonElement
                If Jx.TryParse(line, e) AndAlso e.ValueKind = JsonValueKind.String Then list.Add(e.GetString())
            Next
            Return list.Skip(Math.Max(0, list.Count - 500)).ToList()
        End Function

        Public Shared Sub AddHistory(projectDir As String, entry As String)
            If String.IsNullOrWhiteSpace(entry) Then Return
            Try
                Dim file = HistoryFile(projectDir)
                IO.File.AppendAllText(file, Jx.Quote(entry) & vbLf)
                If New FileInfo(file).Length > 2_000_000 Then
                    Dim keep = History(projectDir)
                    Paths.WriteText(file, String.Join(vbLf, keep.Skip(keep.Count \ 2).Select(Function(h) Jx.Quote(h))) & vbLf)
                End If
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
            End Try
        End Sub

    End Class

End Namespace
