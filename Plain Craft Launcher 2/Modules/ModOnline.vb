Imports System.Security.Cryptography

'独立定制版服务。结构参考 PCL-CE：配置注入、独立更新源、文件校验、公告去重。
Friend Module ModOnline
    Private ReadOnly Gate As New Object
    Private ReadOnly Config As New Lazy(Of JObject)(AddressOf ReadConfig)
    Private Latest As JObject
    Private StagedFile As String

    Private Function ReadConfig() As JObject
        Dim FileName = IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "PCL.Online.json")
        If IO.File.Exists(FileName) Then Return JObject.Parse(IO.File.ReadAllText(FileName))
        Using Stream = Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("PCL.Online.json")
            If Stream Is Nothing Then Return New JObject
            Using Reader As New IO.StreamReader(Stream)
                Return JObject.Parse(Reader.ReadToEnd())
            End Using
        End Using
    End Function

    Friend Function OnlineSetting(Key As String, EnvironmentName As String) As String
        Dim Value = Environment.GetEnvironmentVariable(EnvironmentName)
        If Not String.IsNullOrWhiteSpace(Value) Then Return Value.Trim()
        Return If(Config.Value(Key)?.ToString(), "").Trim()
    End Function

    Friend Sub RequireMicrosoftClient()
        If String.IsNullOrWhiteSpace(OAuthClientId) Then
            Throw New Exception("$尚未配置微软登录应用。请由启动器发布者在 PCL.Online.json 中填写 MicrosoftClientId，或设置 PCL_MS_CLIENT_ID 后重启启动器。PCL-CE 同样在发布构建时注入此标识。")
        End If
    End Sub

    Friend Function ParseHttps(Value As String) As Uri
        Dim Address As Uri = Nothing
        If Not Uri.TryCreate(Value, UriKind.Absolute, Address) OrElse Address.Scheme <> Uri.UriSchemeHttps OrElse Address.UserInfo <> "" Then Return Nothing
        Return Address
    End Function

    Private Function Https(Value As String) As String
        Dim Address = ParseHttps(Value)
        If Address Is Nothing Then Throw New Exception("联网配置中的地址必须是 HTTPS URL。")
        Return Address.AbsoluteUri
    End Function

    Private Function Fetch(Key As String, Env As String) As JObject
        Dim Address = OnlineSetting(Key, Env)
        If Address = "" Then Return Nothing
        Return JObject.Parse(NetRequestByClientRetry(Https(Address)))
    End Function

    Friend Function DefaultOnlineUpdateMirrorUrl() As String
        Return OnlineSetting("S3UpdateUrl", "PCL_S3_UPDATE_URL")
    End Function

    Private Function OnlineUpdateMirrorEnabled() As Boolean
        Return Settings.Get(Of Boolean)("SystemUpdateS3Enabled") AndAlso
            String.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PCL_UPDATE_URL"))
    End Function

    Private Function ReadUpdate(Optional CheckBoth As Boolean = False) As JObject
        Dim GitHubUrl = OnlineSetting("UpdateUrl", "PCL_UPDATE_URL")
        Dim MirrorUrl = ""
        If OnlineUpdateMirrorEnabled() Then
            Dim CustomUrl = Settings.Get(Of String)("SystemUpdateS3Url").Trim()
            MirrorUrl = If(CustomUrl = "", DefaultOnlineUpdateMirrorUrl(), CustomUrl)
        End If
        Dim Mirror As JObject = Nothing
        If MirrorUrl <> "" Then
            Try
                Mirror = ReadUpdateManifest(MirrorUrl, 6000)
                UpdateHash(Mirror)
                Https(Mirror.Value(Of String)("mirrorDownloadUrl"))
                Https(Mirror.Value(Of String)("downloadUrl"))
            Catch ex As Exception When Not ex.IsCanceled
                Mirror = Nothing
            End Try
        End If

        Dim Data = Mirror
        If GitHubUrl <> "" AndAlso (Mirror Is Nothing OrElse CheckBoth OrElse Mirror.Value(Of Integer)("versionCode") <= VersionCode) Then
            Try
                Dim GitHub = ReadUpdateManifest(GitHubUrl, If(Mirror Is Nothing, 0, 6000))
                If Mirror Is Nothing OrElse GitHub.Value(Of Integer)("versionCode") > Mirror.Value(Of Integer)("versionCode") Then
                    Data = GitHub
                End If
            Catch ex As Exception When Mirror IsNot Nothing AndAlso Not ex.IsCanceled
                'S3 可用时，备用清单的错误不影响版本检查。
            End Try
        End If
        If Data IsNot Nothing Then Latest = Data
        Return Data
    End Function

    Private Function ReadUpdateManifest(Address As String, Timeout As Integer) As JObject
        Dim Url = Https(Address)
        Dim Data = JObject.Parse(If(Timeout = 0, NetRequestByClientRetry(Url),
                                   NetRequestByClient(Url, Timeout:=Timeout, MakeLog:=False)))
        Dim Product = OnlineSetting("ProductId", "PCL_PRODUCT_ID")
        If Product = "" OrElse Data.Value(Of String)("productId") <> Product Then Throw New Exception("更新清单与当前定制版的 ProductId 不匹配。")
        If Data("versionCode") Is Nothing OrElse Data.Value(Of Integer)("versionCode") < 1 Then Throw New Exception("更新清单缺少有效版本号。")
        Return Data
    End Function

    Friend Function OnlineIsNewest() As Boolean?
        If Latest Is Nothing Then Return Nothing
        Return Latest.Value(Of Integer)("versionCode") <= VersionCode
    End Function

    Friend Sub OnlineCheck(Manual As Boolean)
        SyncLock Gate
            Try
                Dim Data = ReadUpdate()
                If Data Is Nothing Then
                    If Manual Then Hint("尚未配置定制版更新源，请在 PCL.Online.json 中填写 UpdateUrl。")
                    Return
                End If
                If Data.Value(Of Integer)("versionCode") <= VersionCode Then
                    If Manual Then Hint("当前已是最新版！", HintType.Green)
                    Return
                End If
                Dim Mode = Settings.Get(Of Integer)("SystemSystemUpdate")
                If Not Manual AndAlso (Mode = 3 OrElse (Mode = 2 AndAlso Not Data.Value(Of Boolean)("important"))) Then Return
                If Manual OrElse Mode <> 0 Then
                    If MyMsgBox($"发现新版本：{Data.Value(Of String)("versionName")}{vbCrLf}{Data.Value(Of String)("changelog")}", "启动器更新", "下载更新", "取消") <> 1 Then Return
                End If
                Dim Target = IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "PCL", "CustomUpdate", Guid.NewGuid().ToString("N"), "Launcher.exe")
                DownloadVerified(Data, Target)
                StagedFile = Target
                IsUpdateWaitingRestart = True
                Hint("更新已下载并校验，将在退出启动器后安装。", HintType.Green)
            Catch ex As Exception
                Logger.Error(ex, "检查或下载定制版更新失败", If(Manual, LogBehavior.Alert, LogBehavior.Toast))
            End Try
        End SyncLock
    End Sub

    Private Function UpdateHash(Data As JObject) As String
        Dim Hash = Data.Value(Of String)("sha256")
        If Hash Is Nothing OrElse Not System.Text.RegularExpressions.Regex.IsMatch(Hash, "\A[0-9a-fA-F]{64}\z") Then Throw New Exception("更新清单缺少有效 SHA-256。")
        Return Hash
    End Function

    Private Sub DownloadVerified(Data As JObject, Target As String, Optional Loader As LoaderBase = Nothing)
        Dim Hash = UpdateHash(Data)
        Dim MirrorUrl = Data.Value(Of String)("mirrorDownloadUrl")
        If OnlineUpdateMirrorEnabled() AndAlso Not String.IsNullOrWhiteSpace(MirrorUrl) Then
            Try
                DownloadUpdateFile(MirrorUrl, Hash, Target, Loader)
                Return
            Catch ex As Exception When Not ex.IsCanceled
                '使用同一个清单和哈希从备用地址下载。
            End Try
        End If
        DownloadUpdateFile(Data.Value(Of String)("downloadUrl"), Hash, Target, Loader)
    End Sub

    Private Sub DownloadUpdateFile(Address As String, Hash As String, Target As String, Loader As LoaderBase)
        Dim Url = Https(Address)
        IO.Directory.CreateDirectory(IO.Path.GetDirectoryName(Target))
        Try
            NetDownloadByLoader(Url, Target, Loader)
            Using Algorithm = SHA256.Create(), Input = IO.File.OpenRead(Target)
                Dim Actual = BitConverter.ToString(Algorithm.ComputeHash(Input)).Replace("-", "")
                If Not Actual.Equals(Hash, StringComparison.OrdinalIgnoreCase) Then Throw New Exception("更新文件 SHA-256 校验失败。")
            End Using
        Catch
            If IO.File.Exists(Target) Then IO.File.Delete(Target)
            Throw
        End Try
    End Sub

    Private Const OnlineReplaceArgument As String = "--update"

    Friend Function IsOnlineReplaceArgument(Argument As String) As Boolean
        '已发布的旧版使用没有前缀的 update 启动更新进程。
        Return String.Equals(Argument, OnlineReplaceArgument, StringComparison.Ordinal) OrElse
            String.Equals(Argument, "update", StringComparison.Ordinal)
    End Function

    Friend Sub OnlineRestart(Restart As Boolean)
        If Not IsUpdateWaitingRestart OrElse Not IO.File.Exists(StagedFile) Then Return
        Dim Args = $"{OnlineReplaceArgument} {Process.GetCurrentProcess().Id} ""{PathExe}"" ""{StagedFile}"" {Restart}"
        Process.Start(New ProcessStartInfo(StagedFile, Args) With {.UseShellExecute = False, .CreateNoWindow = True, .WindowStyle = ProcessWindowStyle.Hidden})
        IsUpdateWaitingRestart = False
        If Restart Then RunInUi(Sub() FrmMain.EndProgram(False))
    End Sub

    Friend Sub OnlineReplace(ProcessId As Integer, OldName As String, NewName As String, Restart As Boolean, RunningExe As String)
        '只允许由正在运行的更新文件替换同一安装目录中的启动器。
        Dim Source = IO.Path.GetFullPath(NewName)
        Dim Destination = IO.Path.GetFullPath(OldName)
        Dim ExpectedRoot = IO.Path.Combine(IO.Path.GetDirectoryName(Destination), "PCL", "CustomUpdate") & IO.Path.DirectorySeparatorChar
        If Not Source.Equals(IO.Path.GetFullPath(RunningExe), StringComparison.OrdinalIgnoreCase) OrElse
            Not Source.StartsWith(ExpectedRoot, StringComparison.OrdinalIgnoreCase) OrElse
            Not Destination.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) Then Throw New Exception("无效的更新替换路径。")
        Try
            Using Previous = Process.GetProcessById(ProcessId)
                If Not Previous.WaitForExit(60000) Then Throw New Exception("等待启动器退出超时。")
            End Using
        Catch ex As ArgumentException
            '原进程已经退出。
        End Try
        Dim Pending = Destination & ".update-new"
        IO.File.Copy(Source, Pending, True)
        IO.File.Replace(Pending, Destination, Nothing, True)
        If Restart Then Process.Start(New ProcessStartInfo(Destination) With {.UseShellExecute = True})
    End Sub

    Friend Sub OnlineDownloadLatest(Loader As LoaderBase)
        SyncLock Gate
            Dim Data = ReadUpdate(CheckBoth:=True)
            If Data Is Nothing Then Throw New Exception("尚未配置定制版更新源，无法下载打包用启动器。")
            DownloadVerified(Data, PathTemp & "Custom-Latest.exe", Loader)
        End SyncLock
    End Sub

    Friend Sub OnlineLoad()
        If Settings.Get(Of Integer)("SystemSystemUpdate") <> 3 Then OnlineCheck(False)
        If Settings.Get(Of Integer)("SystemSystemActivity") = 2 Then Return
        Try
            Dim Data = Fetch("AnnouncementUrl", "PCL_ANNOUNCEMENT_URL")
            If Data Is Nothing Then Return
            Dim Product = OnlineSetting("ProductId", "PCL_PRODUCT_ID")
            If Product = "" OrElse Data.Value(Of String)("productId") <> Product Then Throw New Exception("公告清单与当前定制版不匹配。")
            Dim StateFile = IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "PCL", "CustomAnnouncements.json")
            Dim Seen As New HashSet(Of String)(StringComparer.Ordinal)
            If IO.File.Exists(StateFile) Then Seen.UnionWith(JArray.Parse(IO.File.ReadAllText(StateFile)).ToObject(Of String())())
            For Each Item As JObject In CType(Data("announcements"), JArray)
                Dim Id = Product & ":" & Item.Value(Of String)("id")
                If String.IsNullOrWhiteSpace(Item.Value(Of String)("id")) OrElse Seen.Contains(Id) Then Continue For
                If Settings.Get(Of Integer)("SystemSystemActivity") = 1 AndAlso Not Item.Value(Of Boolean)("important") Then Continue For
                MyMsgBox(Item.Value(Of String)("text"), If(Item.Value(Of String)("title"), "启动器公告"))
                Seen.Add(Id)
                IO.Directory.CreateDirectory(IO.Path.GetDirectoryName(StateFile))
                IO.File.WriteAllText(StateFile, New JArray(Seen.ToArray()).ToString())
            Next
        Catch ex As Exception
            Logger.Error(ex, "获取联网公告失败")
        End Try
    End Sub
End Module
