Option Strict On
Option Explicit On
Option Infer On

Namespace Core

    Friend NotInheritable Class AppInfo

        Public Shared ReadOnly Property Version As String
            Get
                Dim v = GetType(AppInfo).Assembly.GetName().Version
                Return If(v Is Nothing, "2.0.0", $"{v.Major}.{v.Minor}.{v.Build}")
            End Get
        End Property

        Public Const Name As String = "Agentic CLI"

    End Class

End Namespace
