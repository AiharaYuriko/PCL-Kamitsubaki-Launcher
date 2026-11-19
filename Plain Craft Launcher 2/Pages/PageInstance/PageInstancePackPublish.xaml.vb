Imports PCL.Modpack
Imports System.IO
Imports System.Net.Http
Imports System.Threading.Tasks
Imports System.Windows.Controls

Public Class PageInstancePackPublish
    Inherits MyPageRight

    Private ReadOnly Target As McInstance
    Private ReadOnly Publisher As PackPublisher
    Private Busy As Boolean
    Private DraftFiles As List(Of PackPublishFile)
    Private PublishedAddress As String

    Public ReadOnly Property InstancePath As String
        Get
            Return Target.PathVersion
        End Get
    End Property

    Public Sub New(Instance As McInstance)
        InitializeComponent()
        Target = Instance
        Publisher = New PackPublisher(Target.PathVersion)
        PublishOperations.ItemsSource = PackPublisher.Operations
        AddHandler PublishScan.Click, Async Sub() Await ScanPublisher()
        AddHandler PublishImportFiles.Click, Async Sub() Await ImportPublisherFiles()
        AddHandler PublishBaseline.Click, Async Sub() Await RecordBaseline()
        AddHandler PublishMaps.Click, Async Sub() Await ImportPublisherMap()
        AddHandler PublishWaypoints.Click, Async Sub() Await ImportPublisherWaypoints()
        AddHandler PublishSelectZip.Click, Sub() MarkZipBackups()
        AddHandler PublishNow.Click, Sub() SubmitPublication(False)
        AddHandler PublishExport.Click, Sub() SubmitPublication(True)
        AddHandler PublishCopy.Click, Sub() Clipboard.SetText(PublishedAddress)
        AddHandler PublishMirror.Change, Sub() PublishMirrorCredentials.IsEnabled = PublishMirror.Checked
        AddHandler PublishFolders.TextChanged, Sub() InvalidateDraft()
        AddHandler PublishFilePaths.TextChanged, Sub() InvalidateDraft()
        Try
            Dim Saved = Publisher.Settings
            PackName.Text = If(CStr(Saved("name")), Target.Name)
            PackId.Text = CStr(Saved("id"))
            PublishChannel.Text = If(CStr(Saved("channel")), "stable")
            PublishFolders.Text = If(CStr(Saved("folders")), String.Join("; ", PackPublisher.DefaultFolders))
            PublishFilePaths.Text = If(CStr(Saved("files")), "")
            PublishAccount.Text = CStr(Saved("account"))
            PublishBucket.Text = CStr(Saved("bucket"))
            Dim SavedRoot = CStr(Saved("root"))
            PublishRoot.Text = If(String.IsNullOrWhiteSpace(SavedRoot), ModPackUpdate.DefaultPackSource, SavedRoot)
            Dim Credentials = Publisher.ReadCredentials()
            PublishAccess.Text = CStr(Credentials("access"))
            PublishSecret.Password = If(CStr(Credentials("secret")), "")
            PublishMirror.Checked = If(CType(Saved("sync_mirror"), Boolean?), True)
            PublishMirrorCredentials.IsEnabled = PublishMirror.Checked
            PublishMirrorBucket.Text = If(CStr(Saved("mirror_bucket")), PackS3MirrorSettings.DefaultBucket)
            PublishMirrorRoot.Text = If(CStr(Saved("mirror_root")), PackS3MirrorSettings.DefaultSourceAddress)
            Dim MirrorCredentials = Publisher.ReadCredentials(mirror:=True)
            PublishMirrorAccess.Text = CStr(MirrorCredentials("access"))
            PublishMirrorSecret.Password = If(CStr(MirrorCredentials("secret")), "")
            PublishRemember.Checked = If(CType(Saved("remember_credentials"), Boolean?), Credentials.HasValues OrElse MirrorCredentials.HasValues)
            Dim Latest = Publisher.Latest
            PublishedAddress = PackPublisher.PublishedSource(Latest)
            If Not String.IsNullOrEmpty(PublishedAddress) Then
                PublishStatus.Text = "最近发布 " & CStr(Latest("release")("id")) & " · " & PublishedAddress
            Else
                PublishStatus.Text = If(Publisher.HasBaseline, "已保存旧整合包基线。", "请扫描原始实例或导入指定文件，再保存基线。")
            End If
        Catch ex As Exception
            PublishStatus.Text = "加载发布项目失败：" & ex.Message
        End Try
        AddHandler PublishRemember.Change, Sub() SaveRememberChoice()
        AddHandler PublishAccess.TextChanged, Sub() SaveEditedCredentials(False)
        AddHandler PublishSecret.PasswordChanged, Sub() SaveEditedCredentials(False)
        AddHandler PublishMirrorAccess.TextChanged, Sub() SaveEditedCredentials(True)
        AddHandler PublishMirrorSecret.PasswordChanged, Sub() SaveEditedCredentials(True)
        UpdatePublisherControls()
    End Sub

    Private Sub SaveRememberChoice()
        Try
            SaveCurrentCredentials(False)
            SaveCurrentCredentials(True)
            Dim Saved = Publisher.Settings
            Saved("remember_credentials") = PublishRemember.Checked
            Publisher.SaveSettings(Saved)
        Catch ex As Exception
            PublishStatus.Text = "无法保存凭据设置：" & ex.Message
        End Try
    End Sub

    Private Sub SaveEditedCredentials(Mirror As Boolean)
        If Not PublishRemember.Checked Then Return
        Try
            SaveCurrentCredentials(Mirror)
        Catch ex As Exception
            PublishStatus.Text = "无法保存发布凭据：" & ex.Message
        End Try
    End Sub

    Private Sub SaveCurrentCredentials(Mirror As Boolean)
        If Not PublishRemember.Checked Then
            Publisher.SaveCredentials("", "", Mirror)
        ElseIf Mirror Then
            Publisher.SaveCredentials(PublishMirrorAccess.Text.Trim(), PublishMirrorSecret.Password, mirror:=True)
        Else
            Publisher.SaveCredentials(PublishAccess.Text.Trim(), PublishSecret.Password)
        End If
    End Sub

    Private Sub SetBusy(Value As Boolean)
        Busy = Value
        UpdatePublisherControls()
    End Sub

    Private Sub UpdatePublisherControls()
        PublishCard.IsEnabled = Not Busy
        PublishBaseline.IsEnabled = Not Busy AndAlso Not Publisher.HasBaseline AndAlso DraftFiles IsNot Nothing
        PublishNow.IsEnabled = Not Busy AndAlso Publisher.HasBaseline AndAlso DraftFiles IsNot Nothing
        PublishExport.IsEnabled = PublishNow.IsEnabled
        PublishSelectZip.IsEnabled = Not Busy AndAlso DraftFiles IsNot Nothing
        PublishCopy.IsEnabled = Not Busy AndAlso Not String.IsNullOrEmpty(PublishedAddress)
    End Sub

    Private Sub InvalidateDraft()
        DraftFiles = Nothing
        PublishFiles.ItemsSource = Nothing
        UpdatePublisherControls()
    End Sub

    Private Function SelectedFilePaths() As String()
        Return PublishFilePaths.Text.Split({ChrW(13), ChrW(10)}, StringSplitOptions.RemoveEmptyEntries)
    End Function

    Private Async Function ImportPublisherFiles() As Task
        If Busy Then Return
        Dim Selected = Dialogs.SelectFile("选择当前实例内要导入的文件", True, Target.PathVersion)
        If Selected.Count = 0 Then Return
        Try
            Dim Paths = SelectedFilePaths().Concat(Selected.Select(Function(File) Publisher.GetImportPath(File)))
            PublishFilePaths.Text = String.Join(vbCrLf, Paths.Distinct(StringComparer.OrdinalIgnoreCase))
        Catch ex As Exception
            PublishStatus.Text = ex.Message
            Return
        End Try
        Await ScanPublisher()
    End Function

    Private Async Function ScanPublisher() As Task
        If Busy Then Return
        Dim Folders = PublishFolders.Text.Split({ChrW(13), ChrW(10), ";"c}, StringSplitOptions.RemoveEmptyEntries)
        Dim Selected = SelectedFilePaths()
        SetBusy(True)
        PublishStatus.Text = "正在扫描客户端文件并匹配平台版本……"
        Try
            DraftFiles = Await Task.Run(
                Function()
                    ModPackUpdate.CheckIdle(Target)
                    Dim Files = Publisher.Scan(Folders, files:=Selected)
                    If Publisher.HasBaseline Then ResolveModSources(Files)
                    Return Files
                End Function)
            PublishFiles.ItemsSource = DraftFiles
            PublishStatus.Text = If(Publisher.HasBaseline,
                $"扫描完成，共 {DraftFiles.Count} 项。请选择发布策略和可公开分发的 ZIP 备用 Mod。",
                $"找到 {DraftFiles.Count} 项基线候选。确认这是玩家共用的旧包后，点击保存旧包基线。")
            If DraftFiles.Any(Function(f) f.Change = "已移出") Then
                PublishStatus.Text &= vbCrLf & "此次发布使用完整文件清单。「已移出」的旧受管文件可能在玩家更新时清理。"
            End If
        Catch ex As Exception
            DraftFiles = Nothing
            PublishFiles.ItemsSource = Nothing
            PublishStatus.Text = ex.Message
        Finally
            SetBusy(False)
        End Try
    End Function

    Private Async Function RecordBaseline() As Task
        If Busy OrElse DraftFiles Is Nothing OrElse Publisher.HasBaseline Then Return
        PublishFiles.CommitEdit(DataGridEditingUnit.Cell, True)
        PublishFiles.CommitEdit(DataGridEditingUnit.Row, True)
        If MyMsgBox("将预览中的文件保存为旧版迁移基线？请选择玩家原先共用的未经修改的整合包。", "保存旧包基线", "保存", "取消") <> 1 Then Return
        SetBusy(True)
        Try
            Dim Snapshot = DraftFiles
            Await Task.Run(Sub()
                               ModPackUpdate.CheckIdle(Target)
                               Publisher.SaveBaseline(Snapshot)
                           End Sub)
            DraftFiles = Nothing
            PublishFiles.ItemsSource = Nothing
            PublishStatus.Text = "旧包基线已保存。调整此实例后可重新扫描或导入指定文件。"
        Catch ex As Exception
            PublishStatus.Text = ex.Message
        Finally
            SetBusy(False)
        End Try
    End Function

    Private Async Function ImportPublisherMap() As Task
        If Busy Then Return
        Dim Folder = Dialogs.SelectFolder("选择当前实例内的 Xaero 地图实例目录", False).FirstOrDefault()
        If String.IsNullOrEmpty(Folder) Then Return
        Dim Version = MyMsgBoxInput("输入服务器地图版本", "新版地图会单独保存，玩家可在 Xaero 世界地图中切换。", HintText:="例如 season-2")
        If String.IsNullOrWhiteSpace(Version) Then Return
        Await ImportMapData(Function() Publisher.ImportXaeroMap(Folder, Version.Trim()))
    End Function

    Private Async Function ImportPublisherWaypoints() As Task
        If Busy Then Return
        Dim File = Dialogs.SelectFile("请选择要公开分发的 Xaero 航点子世界文件", False, filter:={({"txt"}, "Xaero 航点文件")}).FirstOrDefault()
        If String.IsNullOrEmpty(File) Then Return
        Await ImportMapData(Function() Publisher.ImportXaeroWaypoints(File))
    End Function

    Private Async Function ImportMapData(Import As Func(Of String)) As Task
        SetBusy(True)
        Try
            Dim Destination = Await Task.Run(
                Function()
                    ModPackUpdate.CheckIdle(Target)
                    Return Import()
                End Function)
            DraftFiles = Nothing
            PublishFiles.ItemsSource = Nothing
            PublishStatus.Text = "地图已加入发布项目，请重新扫描。" & vbCrLf & Destination
        Catch ex As Exception
            PublishStatus.Text = ex.Message
        Finally
            SetBusy(False)
        End Try
    End Function

    Private Sub MarkZipBackups()
        If DraftFiles Is Nothing OrElse MyMsgBox("确认扫描出的所有 Mod 均可通过你的公开下载地址再分发？",
                                                "标记 ZIP 备用", "确认", "取消") <> 1 Then Return
        PublishFiles.CommitEdit(DataGridEditingUnit.Cell, True)
        PublishFiles.CommitEdit(DataGridEditingUnit.Row, True)
        For Each Entry In DraftFiles.Where(Function(f) f.Category = "mod")
            Entry.EmbedMod = True
        Next
        PublishFiles.Items.Refresh()
    End Sub

    Private Sub SubmitPublication(ExportOnly As Boolean)
        If Busy OrElse DraftFiles Is Nothing OrElse Not Publisher.HasBaseline Then Return
        If Not PublishFiles.CommitEdit(DataGridEditingUnit.Cell, True) OrElse Not PublishFiles.CommitEdit(DataGridEditingUnit.Row, True) Then Return
        Dim Output As String = Nothing
        If ExportOnly Then
            Output = Dialogs.SaveFile("选择测试整合包保存路径", "pcl-test-pack.zip", filter:={("zip", "整合包 ZIP")})
            If String.IsNullOrEmpty(Output) Then Return
        Else
            Dim Message = If(PublishMirror.Checked, "发布到 R2 后自动同步私有 S3，并验证备用签名入口。",
                             "上传完整 ZIP 与清单并切换正式频道。")
            Dim RemovedCount = DraftFiles.Where(Function(f) f.Change = "已移出").Count()
            If RemovedCount > 0 Then Message &= vbCrLf & $"预览中有 {RemovedCount} 项「已移出」。玩家更新时可能清理这些旧受管文件。"
            If MyMsgBox(Message, "发布整合包", "发布", "取消") <> 1 Then Return
        End If
        Dim PreviewFiles = DraftFiles
        Dim Info As New PackPublishDetails With {
            .PackId = PackId.Text.Trim(), .Name = PackName.Text.Trim(), .ReleaseId = PublishReleaseId.Text.Trim(),
            .Channel = PublishChannel.Text.Trim(), .Notes = PublishNotes.Text, .AllowDowngrade = PublishDowngrade.Checked
        }
        Dim Settings As New PackR2Settings With {
            .Account = PublishAccount.Text.Trim(), .Bucket = PublishBucket.Text.Trim(), .PublicRoot = PublishRoot.Text.Trim(),
            .AccessKey = PublishAccess.Text.Trim(), .SecretKey = PublishSecret.Password
        }
        Dim MirrorSettings = If(PublishMirror.Checked AndAlso Not ExportOnly, New PackS3MirrorSettings With {
            .Bucket = PublishMirrorBucket.Text.Trim(), .SourceAddress = PublishMirrorRoot.Text.Trim(),
            .AccessKey = PublishMirrorAccess.Text.Trim(), .SecretKey = PublishMirrorSecret.Password
        }, Nothing)
        Dim Persist = New JObject From {{"name", Info.Name}, {"id", Info.PackId}, {"channel", Info.Channel}, {"folders", PublishFolders.Text}, {"files", PublishFilePaths.Text},
            {"account", Settings.Account}, {"bucket", Settings.Bucket}, {"root", Settings.PublicRoot}, {"sync_mirror", PublishMirror.Checked},
            {"mirror_bucket", PublishMirrorBucket.Text.Trim()}, {"mirror_root", PublishMirrorRoot.Text.Trim()},
            {"remember_credentials", PublishRemember.Checked}}
        SetBusy(True)
        Dim Worker As New RedoableWorker(
            Sub()
                Try
                    ModPackUpdate.CheckIdle(Target)
                    Dim Runtime = ModPackUpdate.ReadPackRuntime(Target)
                    Info.Minecraft = Runtime.MinecraftVersion
                    Info.NeoForge = Runtime.ModLoaderVersions(ModLoaders.NeoForge)
                    Publisher.SaveSettings(Persist)
                    If ExportOnly Then
                        Publisher.Export(Info, PreviewFiles, Output)
                        RunInUi(Sub() PublishStatus.Text = "测试 ZIP 已导出：" & Output)
                    Else
                        Dim Result = New PackR2Publisher(Publisher).Publish(Info, Settings, PreviewFiles, Sub(Message) RunInUi(Sub() PublishStatus.Text = Message), MirrorSettings)
                        RunInUi(Sub()
                                    PublishedAddress = Result.SourceAddress
                                    PublishStatus.Text = "R2 发布成功 · " & PublishedAddress
                                    If MirrorSettings IsNot Nothing Then
                                        If Result.MirrorError Is Nothing Then
                                            PublishStatus.Text &= vbCrLf & "私有 S3 同步成功 · " & MirrorSettings.SourceAddress
                                        Else
                                            PublishStatus.Text &= vbCrLf & "私有 S3 同步未完成：" & Result.MirrorError & vbCrLf & "可用同一发布 ID 和相同内容重试。"
                                        End If
                                    End If
                                    If Not Result.SupportsRanges Then PublishStatus.Text &= vbCrLf & "R2 分段探测失败，玩家需要时可选择下载完整 ZIP。"
                                End Sub)
                    End If
                Catch ex As Exception
                    RunInUi(Sub() PublishStatus.Text = "发布任务未完成：" & ex.Message)
                End Try
            End Sub)
        AddHandler Worker.Stopped, Sub() RunInUi(Sub() SetBusy(False))
        If McInstanceLock.TryAcquire(Worker, Target.PathVersion) Then
            Worker.Start()
        Else
            PublishStatus.Text = "此实例正在执行其他任务，请稍后重试。"
            SetBusy(False)
        End If
    End Sub

    Private Shared Sub ResolveModSources(Files As IEnumerable(Of PackPublishFile))
        Dim Mods = Files.Where(Function(f) f.Category = "mod" AndAlso f.LocalPath.Length > 0).
            Select(Function(f) (Entry:=f, Local:=New LocalResourceFile(f.LocalPath))).ToList()
        For Start As Integer = 0 To Mods.Count - 1 Step 80
            Dim Batch = Mods.Skip(Start).Take(80).ToList()
            Try
                Dim Query = New JObject From {{"algorithm", "sha1"}, {"hashes", New JArray(Batch.Select(Function(m) m.Local.ModrinthHash).Distinct())}}
                Dim Versions As JObject = CType(DlModRequest("https://api.modrinth.com/v2/version_files", HttpMethod.Post, Query.ToString(), "application/json"), JObject)
                For Each Entry In Batch
                    Dim V = Versions(Entry.Local.ModrinthHash)
                    If V Is Nothing Then Continue For
                    Dim File = V("files").FirstOrDefault(Function(f) CStr(f("hashes")("sha1")) = Entry.Local.ModrinthHash AndAlso CLng(f("size")) = Entry.Entry.Size)
                    If File IsNot Nothing Then AddPublisherSource(Entry.Entry, "modrinth", CStr(V("project_id")), CStr(V("id")), CStr(File("url")))
                Next
            Catch ex As Exception
                Logger.Warn(ex, "发布预览获取 Modrinth 文件信息失败")
            End Try
            Try
                Dim Query = New JObject From {{"fingerprints", New JArray(Batch.Select(Function(m) m.Local.CurseForgeHash).Distinct())}}
                Dim Matches = DlModRequest("https://api.curseforge.com/v1/fingerprints/432", HttpMethod.Post, Query.ToString(), "application/json")("data")("exactMatches")
                For Each Match In Matches
                    Dim File = Match("file")
                    If File Is Nothing OrElse String.IsNullOrWhiteSpace(CStr(File("downloadUrl"))) Then Continue For
                    For Each Entry In Batch.Where(Function(m) m.Local.CurseForgeHash = CUInt(File("fileFingerprint")) AndAlso m.Entry.Size = CLng(File("fileLength")))
                        If File("hashes") Is Nothing OrElse Not File("hashes").Any(Function(h) CInt(h("algo")) = 1 AndAlso
                              String.Equals(CStr(h("value")), Entry.Local.ModrinthHash, StringComparison.OrdinalIgnoreCase)) Then Continue For
                        AddPublisherSource(Entry.Entry, "curseforge", CStr(File("modId")), CStr(File("id")), CStr(File("downloadUrl")))
                    Next
                Next
            Catch ex As Exception
                Logger.Warn(ex, "发布预览获取 CurseForge 文件信息失败")
            End Try
        Next
        For Each Entry In Mods
            Entry.Entry.Platform = If(Entry.Entry.Urls.Count > 0, String.Join(" / ", Entry.Entry.Sources.Select(Function(s) If(CStr(s("platform")) = "modrinth", "MR", "CF"))), "未匹配")
        Next
    End Sub

    Private Shared Sub AddPublisherSource(Entry As PackPublishFile, Platform As String, Project As String, Version As String, Address As String)
        PackProtocol.Https(Address)
        If Not Entry.Urls.Contains(Address) Then Entry.Urls.Add(Address)
        Entry.Sources.Add(New JObject From {{"platform", Platform}, {"project", Project}, {"version", Version}})
    End Sub
End Class
