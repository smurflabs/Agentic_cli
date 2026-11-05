Option Strict On
Option Explicit On
Option Infer On

Imports System.Runtime.InteropServices

Namespace Tools

    ''' <summary>
    ''' Windows: each Bash command gets a job object of its own, so KillShell stops everything it started - even what outlived
    ''' its shell, which a process tree cannot reach once a process in between has ended. Closing the job stops nothing (what a
    ''' command started on purpose, like a server in its own window, may outlive the CLI as before). Elsewhere these do nothing.
    ''' </summary>
    Friend NotInheritable Class CommandGroups

        Private Sub New()
        End Sub

        ''' <summary>The command's own job (Zero outside Windows or when Windows refuses).</summary>
        Public Shared Function ForCommand(proc As Process) As IntPtr
            If Not OperatingSystem.IsWindows() OrElse proc Is Nothing Then Return IntPtr.Zero
            Dim own = IntPtr.Zero
            Try
                own = CreateJobObject(IntPtr.Zero, Nothing)
                If own = IntPtr.Zero Then Return IntPtr.Zero
                If AssignProcessToJobObject(own, proc.Handle) Then Return own
            Catch ex As Exception When TypeOf ex Is InvalidOperationException OrElse TypeOf ex Is ComponentModel.Win32Exception OrElse
                                       TypeOf ex Is DllNotFoundException OrElse TypeOf ex Is EntryPointNotFoundException
                ' It has already ended, or there are no job objects here.
            End Try
            If own <> IntPtr.Zero Then CloseHandle(own)
            Return IntPtr.Zero
        End Function

        ''' <summary>Stops every process of a command's job and lets go of it.</summary>
        Public Shared Sub Terminate(group As IntPtr)
            If group = IntPtr.Zero Then Return
            TerminateJobObject(group, 1UI)
            CloseHandle(group)
        End Sub

        ''' <summary>Lets go of a command's job without stopping anything.</summary>
        Public Shared Sub Release(group As IntPtr)
            If group <> IntPtr.Zero Then CloseHandle(group)
        End Sub

        <DllImport("kernel32.dll", CharSet:=CharSet.Unicode, SetLastError:=True)>
        Private Shared Function CreateJobObject(attributes As IntPtr, name As String) As IntPtr
        End Function

        <DllImport("kernel32.dll", SetLastError:=True)>
        Private Shared Function AssignProcessToJobObject(job As IntPtr, process As IntPtr) As <MarshalAs(UnmanagedType.Bool)> Boolean
        End Function

        <DllImport("kernel32.dll", SetLastError:=True)>
        Private Shared Function TerminateJobObject(job As IntPtr, exitCode As UInteger) As <MarshalAs(UnmanagedType.Bool)> Boolean
        End Function

        <DllImport("kernel32.dll", SetLastError:=True)>
        Private Shared Function CloseHandle(handle As IntPtr) As <MarshalAs(UnmanagedType.Bool)> Boolean
        End Function

    End Class

End Namespace
