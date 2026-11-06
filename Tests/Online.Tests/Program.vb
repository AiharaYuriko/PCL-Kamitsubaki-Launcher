Imports System.Security.Cryptography
Imports System.Text

Module Program
    Public OAuthClientId As String = ""
    Public IsUpdateWaitingRestart As Boolean
    Public VersionCode As Integer = 10
    Public PathExe As String = "unused.exe"
    Public PathTemp As String = AppDomain.CurrentDomain.BaseDirectory
    Public FrmMain As New FakeMain
    Public Responses As New Dictionary(Of String, String)
    Public Calls As Integer
    Public Messages As Integer
    Public Downloads As Integer
    Public Failure As Exception
    Public Payload As Byte() = Encoding.UTF8.GetBytes("test launcher payload")

    Sub Assert(Value As Boolean, Message As String)
        If Not Value Then Throw New Exception(Message)
    End Sub
    Sub Main()
        Environment.SetEnvironmentVariable("PCL_PRODUCT_ID", "online-tests")
        Environment.SetEnvironmentVariable("PCL_UPDATE_URL", "https://test.invalid/update")
        Environment.SetEnvironmentVariable("PCL_ANNOUNCEMENT_URL", "https://test.invalid/notice")
        Try
            RequireMicrosoftClient()
            Throw New Exception("Missing client ID accepted")
        Catch ex As Exception When ex.Message.StartsWith("$尚未配置")
        End Try
        OAuthClientId = "configured-client"
        RequireMicrosoftClient()
        Dim Update As New JObject From {{"productId", "wrong"}, {"versionCode", 11}}
        Responses("https://test.invalid/update") = Update.ToString()
        OnlineCheck(True)
        Assert(Failure IsNot Nothing AndAlso Downloads = 0, "Wrong product accepted")
        Failure = Nothing
        Update("productId") = "online-tests"
        Update("sha256") = New String("0"c, 64)
        Update("downloadUrl") = "https://test.invalid/launcher.exe"
        Responses("https://test.invalid/update") = Update.ToString()
        OnlineCheck(True)
        Assert(Failure IsNot Nothing AndAlso Not IsUpdateWaitingRestart, "Hash mismatch staged")
        Failure = Nothing
        Update("sha256") = Convert.ToHexString(SHA256.HashData(Payload))
        Update("downloadUrl") = "http://test.invalid/launcher.exe"
        Responses("https://test.invalid/update") = Update.ToString()
        Dim Before = Downloads
        OnlineCheck(True)
        Assert(Failure IsNot Nothing AndAlso Downloads = Before, "HTTP download accepted")
        Failure = Nothing
        Update("downloadUrl") = "https://test.invalid/launcher.exe"
        Responses("https://test.invalid/update") = Update.ToString()
        OnlineCheck(True)
        Assert(Failure Is Nothing AndAlso IsUpdateWaitingRestart, "Verified update not staged")
        Settings.Values("SystemSystemUpdate") = 3
        Settings.Values("SystemSystemActivity") = 1
        Dim Unique = Guid.NewGuid().ToString()
        Responses("https://test.invalid/notice") = New JObject From {
            {"productId", "online-tests"},
            {"announcements", New JArray(New JObject From {{"id", Unique}, {"important", True}, {"text", "Important"}},
                                       New JObject From {{"id", Unique & "-normal"}, {"important", False}, {"text", "Normal"}})}}.ToString()
        Before = Messages
        OnlineLoad()
        OnlineLoad()
        Assert(Failure Is Nothing AndAlso Messages = Before + 1, "Notice filtering or deduplication failed")
        Settings.Values("SystemSystemActivity") = 2
        Before = Calls
        OnlineLoad()
        Assert(Calls = Before, "Disabled services still access network")
        Dim TestRoot = IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ReplaceTest", Guid.NewGuid().ToString("N"))
        Dim OldFile = IO.Path.Combine(TestRoot, "Launcher.exe")
        Dim NewFile = IO.Path.Combine(TestRoot, "PCL", "CustomUpdate", "test", "Launcher.exe")
        IO.Directory.CreateDirectory(IO.Path.GetDirectoryName(NewFile))
        IO.File.WriteAllText(OldFile, "old")
        IO.File.WriteAllText(NewFile, "new")
        PathExe = NewFile
        OnlineReplace(-1, OldFile, NewFile, False)
        Assert(IO.File.ReadAllText(OldFile) = "new" AndAlso IO.File.ReadAllText(OldFile & ".update-backup") = "old", "Replacement or backup failed")
        Try
            OnlineReplace(-1, OldFile, OldFile, False)
            Throw New Exception("Invalid replacement path accepted")
        Catch ex As Exception When ex.Message = "无效的更新替换路径。"
        End Try
        Console.WriteLine("PASS: client configuration, product isolation, SHA256 rejection/success, HTTPS, announcement filtering/deduplication, disabled services, replacement/backup/path validation.")
    End Sub
    Public Function NetRequestByClientRetry(Url As String) As String
        Calls += 1
        Return Responses(Url)
    End Function
    Public Sub NetDownloadByLoader(Url As String, Target As String, Loader As LoaderBase)
        Downloads += 1
        IO.File.WriteAllBytes(Target, Payload)
    End Sub
    Public Function MyMsgBox(Text As String, Title As String, Optional First As String = "确定", Optional Second As String = "") As Integer
        Messages += 1
        Return 1
    End Function
    Public Sub Hint(Text As String, Optional Kind As HintType = HintType.Green)
    End Sub
    Public Sub RunInUi(Action As Action)
        Action()
    End Sub
End Module
Public Class LoaderBase
End Class
Public Class FakeMain
    Public Sub EndProgram(Value As Boolean)
    End Sub
End Class
Public Enum HintType
    Green
End Enum
Public Enum LogBehavior
    Alert
    Toast
End Enum
Public Module Logger
    Public Sub [Error](Ex As Exception, Message As String, Optional Behavior As LogBehavior = LogBehavior.Alert)
        Failure = Ex
    End Sub
End Module
Public Module Settings
    Public Values As New Dictionary(Of String, Integer) From {{"SystemSystemUpdate", 0}, {"SystemSystemActivity", 0}}
    Public Function [Get](Of T)(Key As String) As T
        Return CType(CObj(Values(Key)), T)
    End Function
End Module
