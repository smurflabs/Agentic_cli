Option Strict On
Option Explicit On
Option Infer On

Imports System.Text.Encodings.Web
Imports System.Text.Json
Imports System.Text.Json.Nodes

Namespace Core

    ''' <summary>Small helpers over System.Text.Json for the shapes the server, the settings files and the tools use.</summary>
    Friend NotInheritable Class Jx

        Public Shared ReadOnly CompactOptions As New JsonSerializerOptions With {.Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping}
        Public Shared ReadOnly PrettyOptions As New JsonSerializerOptions With {.Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, .WriteIndented = True}
        Public Shared ReadOnly Lenient As New JsonDocumentOptions With {.CommentHandling = JsonCommentHandling.Skip, .AllowTrailingCommas = True}

        Public Shared Function Stringify(value As Object, Optional indented As Boolean = False) As String
            If value Is Nothing Then Return "null"
            Return JsonSerializer.Serialize(value, value.GetType(), If(indented, PrettyOptions, CompactOptions))
        End Function

        ''' <summary>A JSON string literal.</summary>
        Public Shared Function Quote(s As String) As String
            Return JsonSerializer.Serialize(If(s, ""), CompactOptions)
        End Function

        Public Shared Function Parse(text As String) As JsonElement
            Using doc = JsonDocument.Parse(If(String.IsNullOrWhiteSpace(text), "{}", text), Lenient)
                Return doc.RootElement.Clone()
            End Using
        End Function

        Public Shared Function TryParse(text As String, ByRef result As JsonElement) As Boolean
            Try
                result = Parse(text)
                Return True
            Catch ex As JsonException
                Return False
            End Try
        End Function

        ''' <summary>A JSON object node from text; an empty object when the text is empty or not an object.</summary>
        Public Shared Function ParseObject(text As String) As JsonObject
            If String.IsNullOrWhiteSpace(text) Then Return New JsonObject()
            Try
                Return If(TryCast(JsonNode.Parse(text, Nothing, Lenient), JsonObject), New JsonObject())
            Catch ex As JsonException
                Return New JsonObject()
            End Try
        End Function

        Public Shared Function ToNode(e As JsonElement) As JsonNode
            If e.ValueKind = JsonValueKind.Undefined Then Return Nothing
            Return JsonNode.Parse(e.GetRawText())
        End Function

        Public Shared Function Has(e As JsonElement, name As String) As Boolean
            Dim v As JsonElement
            Return e.ValueKind = JsonValueKind.Object AndAlso e.TryGetProperty(name, v) AndAlso v.ValueKind <> JsonValueKind.Null
        End Function

        Public Shared Function Prop(e As JsonElement, name As String) As JsonElement
            Dim v As JsonElement
            If e.ValueKind = JsonValueKind.Object AndAlso e.TryGetProperty(name, v) Then Return v
            Return Nothing
        End Function

        Public Shared Function StrOf(e As JsonElement, name As String, Optional fallback As String = "") As String
            Dim v = Prop(e, name)
            Select Case v.ValueKind
                Case JsonValueKind.String : Return v.GetString()
                Case JsonValueKind.Number, JsonValueKind.True, JsonValueKind.False : Return v.GetRawText()
                Case JsonValueKind.Object, JsonValueKind.Array : Return v.GetRawText()
                Case Else : Return fallback
            End Select
        End Function

        Public Shared Function IntOf(e As JsonElement, name As String, Optional fallback As Integer = 0) As Integer
            Dim v = Prop(e, name)
            Dim i As Integer
            If v.ValueKind = JsonValueKind.Number AndAlso v.TryGetInt32(i) Then Return i
            If v.ValueKind = JsonValueKind.Number Then Return CInt(Math.Max(Integer.MinValue, Math.Min(Integer.MaxValue, v.GetDouble())))
            If v.ValueKind = JsonValueKind.String AndAlso Integer.TryParse(v.GetString(), i) Then Return i
            Return fallback
        End Function

        Public Shared Function LongOf(e As JsonElement, name As String, Optional fallback As Long = 0) As Long
            Dim v = Prop(e, name)
            Dim i As Long
            If v.ValueKind = JsonValueKind.Number AndAlso v.TryGetInt64(i) Then Return i
            Return fallback
        End Function

        Public Shared Function DblOf(e As JsonElement, name As String, Optional fallback As Double = 0) As Double
            Dim v = Prop(e, name)
            If v.ValueKind = JsonValueKind.Number Then Return v.GetDouble()
            Return fallback
        End Function

        Public Shared Function BoolOf(e As JsonElement, name As String, Optional fallback As Boolean = False) As Boolean
            Dim v = Prop(e, name)
            If v.ValueKind = JsonValueKind.True Then Return True
            If v.ValueKind = JsonValueKind.False Then Return False
            If v.ValueKind = JsonValueKind.String Then
                Dim s = v.GetString().Trim().ToLowerInvariant()
                If s = "true" OrElse s = "yes" OrElse s = "1" Then Return True
                If s = "false" OrElse s = "no" OrElse s = "0" Then Return False
            End If
            Return fallback
        End Function

        Public Shared Function ArrOf(e As JsonElement, name As String) As List(Of JsonElement)
            Dim v = Prop(e, name)
            If v.ValueKind = JsonValueKind.Array Then Return v.EnumerateArray().ToList()
            Return New List(Of JsonElement)
        End Function

        Public Shared Function StringsOf(e As JsonElement, name As String) As List(Of String)
            Dim v = Prop(e, name)
            Dim list As New List(Of String)
            If v.ValueKind = JsonValueKind.Array Then
                For Each x In v.EnumerateArray()
                    If x.ValueKind = JsonValueKind.String Then list.Add(x.GetString()) Else list.Add(x.GetRawText())
                Next
            ElseIf v.ValueKind = JsonValueKind.String Then
                list.AddRange(v.GetString().Split({","c, " "c}, StringSplitOptions.RemoveEmptyEntries))
            End If
            Return list
        End Function

        Public Shared Function DateOf(e As JsonElement, name As String) As DateTime?
            Dim s = StrOf(e, name, Nothing)
            Dim d As DateTime
            If s IsNot Nothing AndAlso DateTime.TryParse(s, Globalization.CultureInfo.InvariantCulture, Globalization.DateTimeStyles.AdjustToUniversal Or Globalization.DateTimeStyles.AssumeUniversal, d) Then Return d
            Return Nothing
        End Function

        ' ------------------------------------------------------------------ JsonNode helpers (settings files)

        Public Shared Function NodeStr(n As JsonNode, Optional fallback As String = Nothing) As String
            If n Is Nothing Then Return fallback
            Try
                Dim v = TryCast(n, JsonValue)
                If v IsNot Nothing Then
                    Dim s As String = Nothing
                    If v.TryGetValue(s) Then Return s
                    Return v.ToJsonString()
                End If
                Return n.ToJsonString()
            Catch ex As InvalidOperationException
                Return fallback
            End Try
        End Function

        Public Shared Function NodeBool(n As JsonNode, fallback As Boolean) As Boolean
            Dim v = TryCast(n, JsonValue)
            If v Is Nothing Then Return fallback
            Dim b As Boolean
            If v.TryGetValue(b) Then Return b
            Dim s As String = Nothing
            If v.TryGetValue(s) Then
                If s.Equals("true", StringComparison.OrdinalIgnoreCase) Then Return True
                If s.Equals("false", StringComparison.OrdinalIgnoreCase) Then Return False
            End If
            Return fallback
        End Function

        Public Shared Function NodeInt(n As JsonNode, fallback As Integer) As Integer
            Dim v = TryCast(n, JsonValue)
            If v Is Nothing Then Return fallback
            Dim i As Integer
            If v.TryGetValue(i) Then Return i
            Dim d As Double
            If v.TryGetValue(d) Then Return CInt(d)
            Dim s As String = Nothing
            If v.TryGetValue(s) AndAlso Integer.TryParse(s, i) Then Return i
            Return fallback
        End Function

        Public Shared Function NodeStrings(n As JsonNode) As List(Of String)
            Dim list As New List(Of String)
            Dim a = TryCast(n, JsonArray)
            If a IsNot Nothing Then
                For Each x In a
                    Dim s = NodeStr(x)
                    If Not String.IsNullOrWhiteSpace(s) Then list.Add(s)
                Next
            ElseIf n IsNot Nothing Then
                Dim s = NodeStr(n)
                If Not String.IsNullOrWhiteSpace(s) Then list.Add(s)
            End If
            Return list
        End Function

    End Class

End Namespace
