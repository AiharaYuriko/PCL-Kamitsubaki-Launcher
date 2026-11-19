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
        AddHandler PublishBaselineZip.Click, Async Sub() Await ImportBaseline(True)
        AddHandler PublishBaselineFolder.Click, Async Sub() Await ImportBaseline(False)
        AddHandler PublishBaselineSync.Click, Async Sub() Await UpdateCloudBaselines(False)
        AddHandler PublishBaselineUpload.Click, Async Sub() Await UpdateCloudBaselines(True)
        AddHandler PublishMaps.Click, Async Sub() Await ImportPublisherMap()
        AddHandler PublishWaypoints.Click, Async Sub() Await ImportPublisherWaypoints()
        AddHandler PublishSelectZip.Click, Sub() MarkZipBackups()
        AddHandler PublishNow.Click, Sub() SubmitPublication(False)
        AddHandler PublishExport.Click, Sub() SubmitPublication(True)
        AddHandler PublishCopy.Click, Sub() Clipboard.SetText(PublishedAddress)
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
            PublishBaselineBucket.Text = CStr(Saved("baseline_bucket"))
            LoadBaselines(CStr(Saved("baseline_id")))
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
                PublishStatus.Text = If(PublishBaselineChoice.SelectedItem IsNot Nothing, "请选择发布基线，再扫描此实例。", "请导入旧整合包，或扫描当前实例后保存为基线。")
            End If
        Catch ex As Exception
            PublishStatus.Text = "加载发布项目失败：" & ex.Message
        End Try
        AddHandler PublishBaselineChoice.SelectionChanged, Sub() ChangeBaseline()
        AddHandler PackId.TextChanged, Sub()
                                           SaveStorageSettings()
                                           RefreshBaselineChoices()
                                       End Sub
        AddHandler PublishAccount.TextChanged, Sub()
                                                   SaveStorageSettings()
                                                   RefreshBaselineChoices()
                                               End Sub
        AddHandler PublishBucket.TextChanged, Sub() SaveStorageSettings()
        AddHandler PublishBaselineBucket.TextChanged, Sub()
                                                          SaveStorageSettings()
                                                          RefreshBaselineChoices()
                                                      End Sub
        AddHandler PublishRoot.TextChanged, Sub() SaveStorageSettings()
        AddHandler PublishMirrorBucket.TextChanged, Sub() SaveStorageSettings()
        AddHandler PublishMirrorRoot.TextChanged, Sub() SaveStorageSettings()
        AddHandler PublishMirror.Change, Sub()
                                             PublishMirrorCredentials.IsEnabled = PublishMirror.Checked
                                             SaveStorageSettings()
                                         End Sub
        AddHandler PublishRemember.Change, Sub() SaveRememberChoice()
        AddHandler PublishAccess.TextChanged, Sub() SaveEditedCredentials(False)
        AddHandler PublishSecret.PasswordChanged, Sub() SaveEditedCredentials(False)
        AddHandler PublishMirrorAccess.TextChanged, Sub() SaveEditedCredentials(True)
        AddHandler PublishMirrorSecret.PasswordChanged, Sub() SaveEditedCredentials(True)
        UpdatePublisherControls()
    End Sub

    Private Sub SaveStorageSettings()
        Try
            Dim Saved = Publisher.Settings
            CaptureStorageSettings(Saved)
            Publisher.SaveSettings(Saved)
        Catch ex As Exception
            PublishStatus.Text = "无法保存存储桶设置：" & ex.Message
        End Try
    End Sub

    Private Sub CaptureStorageSettings(Saved As JObject)
        Saved("id") = PackId.Text.Trim()
        Saved("account") = PublishAccount.Text.Trim()
        Saved("bucket") = PublishBucket.Text.Trim()
        Saved("baseline_bucket") = PublishBaselineBucket.Text.Trim()
        Saved("root") = PublishRoot.Text.Trim()
        Saved("sync_mirror") = PublishMirror.Checked
        Saved("mirror_bucket") = PublishMirrorBucket.Text.Trim()
        Saved("mirror_root") = PublishMirrorRoot.Text.Trim()
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
        PublishBaseline.IsEnabled = Not Busy AndAlso DraftFiles IsNot Nothing
        PublishBaselineSync.IsEnabled = Not Busy AndAlso PublishBaselineSyncAvailable()
        PublishBaselineUpload.IsEnabled = PublishBaselineSync.IsEnabled AndAlso PublishBaselineChoice.SelectedItem IsNot Nothing
        PublishNow.IsEnabled = Not Busy AndAlso PublishBaselineChoice.SelectedItem IsNot Nothing AndAlso DraftFiles IsNot Nothing
        PublishExport.IsEnabled = PublishNow.IsEnabled
        PublishSelectZip.IsEnabled = Not Busy AndAlso DraftFiles IsNot Nothing
        PublishCopy.IsEnabled = Not Busy AndAlso Not String.IsNullOrEmpty(PublishedAddress)
    End Sub

    Private Sub LoadBaselines(SelectedId As String)
        Dim Scope = If(PublishBaselineSyncAvailable(),
                       PackR2Publisher.BaselineScope(PublishAccount.Text.Trim(), PublishBaselineBucket.Text.Trim(), PackId.Text.Trim()), Nothing)
        Dim Options = Publisher.BaselinesFor(Scope)
        Dim Selected = If(Options.FirstOrDefault(Function(b) b.Id = SelectedId), Options.FirstOrDefault())
        Dim Existing = TryCast(PublishBaselineChoice.ItemsSource, IEnumerable(Of PackBaseline))
        If Existing Is Nothing OrElse Not Existing.SequenceEqual(Options) Then PublishBaselineChoice.ItemsSource = Options
        If Not Object.Equals(Selected, PublishBaselineChoice.SelectedItem) Then PublishBaselineChoice.SelectedItem = Selected
        UpdatePublisherControls()
    End Sub

    Private Sub RefreshBaselineChoices()
        Dim Choice = TryCast(PublishBaselineChoice.SelectedItem, PackBaseline)
        LoadBaselines(Choice?.Id)
    End Sub

    Private Sub ChangeBaseline()
        InvalidateDraft()
        Dim Choice = TryCast(PublishBaselineChoice.SelectedItem, PackBaseline)
        If Choice Is Nothing Then Return
        Try
            Dim Saved = Publisher.Settings
            Saved("baseline_id") = Choice.Id
            Saved("id") = PackId.Text.Trim()
            Publisher.SaveSettings(Saved)
            PublishStatus.Text = $"已选择基线「{Choice.Name}」，请重新扫描此实例。"
        Catch ex As Exception
            PublishStatus.Text = "无法记住基线选择：" & ex.Message
        End Try
    End Sub

    Private Sub InvalidateDraft()
        DraftFiles = Nothing
        PublishFiles.ItemsSource = Nothing
        UpdatePublisherControls()
    End Sub

    Private Function SelectedFolders() As String()
        Return PublishFolders.Text.Split({ChrW(13), ChrW(10), ";"c}, StringSplitOptions.RemoveEmptyEntries)
    End Function

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
        Dim Folders = SelectedFolders()
        Dim Selected = SelectedFilePaths()
        Dim Choice = TryCast(PublishBaselineChoice.SelectedItem, PackBaseline)
        SetBusy(True)
        PublishStatus.Text = "正在扫描客户端文件并匹配平台版本……"
        Try
            DraftFiles = Await Task.Run(
                Function()
                    ModPackUpdate.CheckIdle(Target)
                    Dim Files = Publisher.Scan(Folders, compare:=Choice IsNot Nothing, files:=Selected, baselineId:=Choice?.Id)
                    If Choice IsNot Nothing Then ResolveModSources(Files)
                    Return Files
                End Function)
            PublishFiles.ItemsSource = DraftFiles
            PublishStatus.Text = If(Choice IsNot Nothing,
                $"已对照基线「{Choice?.Name}」扫描，共 {DraftFiles.Count} 项。请选择发布策略和可公开分发的 ZIP 备用 Mod。",
                $"扫描完成，共 {DraftFiles.Count} 项。确认这是旧包后，可以将预览保存为基线。")
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
        If Busy OrElse DraftFiles Is Nothing Then Return
        If Not PublishFiles.CommitEdit(DataGridEditingUnit.Cell, True) OrElse Not PublishFiles.CommitEdit(DataGridEditingUnit.Row, True) Then Return
        Dim Name = MyMsgBoxInput("保存预览为基线", "为当前实例的选中文件快照命名。", HintText:="例如 正式版 1.0")
        If String.IsNullOrWhiteSpace(Name) Then Return
        Dim Snapshot = DraftFiles
        Await SaveBaseline(Function() Publisher.SaveBaseline(Name, Snapshot))
    End Function

    Private Async Function ImportBaseline(FromZip As Boolean) As Task
        If Busy Then Return
        Dim Source = If(FromZip,
            Dialogs.SelectFile("选择完整旧整合包 ZIP", False, filter:={({"zip"}, "整合包 ZIP")}).FirstOrDefault(),
            Dialogs.SelectFolder("选择旧实例目录（包含 mods、config 等文件夹）", False).FirstOrDefault())
        If String.IsNullOrEmpty(Source) Then Return
        Dim Name = MyMsgBoxInput("导入整合包基线", "为这份旧整合包命名，以便发布时选择。", HintText:="例如 测试版 A")
        If String.IsNullOrWhiteSpace(Name) Then Return
        Dim Folders = SelectedFolders()
        Dim Files = SelectedFilePaths()
        Await SaveBaseline(Function() If(FromZip, Publisher.ImportBaselineZip(Name, Source, Folders, Files),
                                               Publisher.ImportBaselineFolder(Name, Source, Folders, Files)))
    End Function

    Private Async Function SaveBaseline(Save As Func(Of PackBaseline)) As Task
        SetBusy(True)
        PublishStatus.Text = "正在生成基线快照……"
        Try
            Dim Baseline = Await Task.Run(Function()
                                              ModPackUpdate.CheckIdle(Target)
                                              Return Save()
                                          End Function)
            LoadBaselines(Baseline.Id)
            If PublishBaselineSyncAvailable() Then
                Try
                    PublishStatus.Text = $"已在本机保存「{Baseline.Name}」，正在上传基线……"
                    Await UploadCloudBaseline(Baseline.Id)
                    PublishStatus.Text = $"基线「{Baseline.Name}」已保存并上传。重新扫描后即可发布。"
                Catch ex As Exception
                    PublishStatus.Text = $"基线「{Baseline.Name}」已保存在本机。上传失败，可用「上传所选基线」重试。{vbCrLf}{ex.Message}"
                End Try
            Else
                PublishStatus.Text = $"基线「{Baseline.Name}」已保存在本机。重新扫描后即可发布。"
            End If
        Catch ex As Exception
            PublishStatus.Text = ex.Message
        Finally
            SetBusy(False)
        End Try
    End Function

    Private Function ReadBaselineStorage() As PackR2Settings
        Dim Bucket = PublishBaselineBucket.Text.Trim()
        If String.Equals(Bucket, PublishBucket.Text.Trim(), StringComparison.OrdinalIgnoreCase) Then
            Throw New InvalidDataException("请使用独立的私有 R2 桶保存云端基线。")
        End If
        Return New PackR2Settings With {
            .Account = PublishAccount.Text.Trim(), .Bucket = Bucket,
            .AccessKey = PublishAccess.Text.Trim(), .SecretKey = PublishSecret.Password
        }
    End Function

    Private Function PublishBaselineSyncAvailable() As Boolean
        Return Not String.IsNullOrWhiteSpace(PublishBaselineBucket.Text)
    End Function

    Private Function UploadCloudBaseline(Id As String) As Task
        Dim Settings = ReadBaselineStorage()
        Dim IdPack = PackId.Text.Trim()
        Dim Cloud = New PackR2Publisher(Publisher)
        Return Task.Run(Sub() Cloud.UploadBaseline(Settings, IdPack, Id))
    End Function

    Private Async Function UpdateCloudBaselines(Upload As Boolean) As Task
        If Busy Then Return
        Dim Choice = TryCast(PublishBaselineChoice.SelectedItem, PackBaseline)
        If Upload AndAlso Choice Is Nothing Then Return
        SetBusy(True)
        Try
            If Upload Then
                PublishStatus.Text = $"正在上传基线「{Choice.Name}」……"
                Await UploadCloudBaseline(Choice.Id)
                PublishStatus.Text = $"基线「{Choice.Name}」已上传。"
            Else
                Dim Settings = ReadBaselineStorage()
                Dim IdPack = PackId.Text.Trim()
                PublishStatus.Text = "正在同步该整合包的云端基线……"
                Dim Count = Await Task.Run(Function() New PackR2Publisher(Publisher).SyncBaselines(Settings, IdPack))
                LoadBaselines(Choice?.Id)
                PublishStatus.Text = $"已同步 {Count} 份云端基线。请选择本次发布的基线，再扫描此实例。"
            End If
        Catch ex As Exception
            PublishStatus.Text = If(Upload, "上传基线失败：", "同步基线失败：") & ex.Message
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
        Dim Choice = TryCast(PublishBaselineChoice.SelectedItem, PackBaseline)
        If Busy OrElse DraftFiles Is Nothing OrElse Choice Is Nothing Then Return
        If Not PublishFiles.CommitEdit(DataGridEditingUnit.Cell, True) OrElse Not PublishFiles.CommitEdit(DataGridEditingUnit.Row, True) Then Return
        Dim Output As String = Nothing
        If ExportOnly Then
            Output = Dialogs.SaveFile("选择测试整合包保存路径", "pcl-test-pack.zip", filter:={("zip", "整合包 ZIP")})
            If String.IsNullOrEmpty(Output) Then Return
        Else
            Dim Message = If(PublishMirror.Checked, "发布到 R2 后自动同步私有 S3，并验证备用签名入口。",
                             "上传完整 ZIP 与清单并切换正式频道。")
            Message &= vbCrLf & $"本次发布使用基线「{Choice.Name}」。"
            Dim RemovedCount = DraftFiles.Where(Function(f) f.Change = "已移出").Count()
            If RemovedCount > 0 Then Message &= vbCrLf & $"预览中有 {RemovedCount} 项「已移出」。玩家更新时可能清理这些旧受管文件。"
            If MyMsgBox(Message, "发布整合包", "发布", "取消") <> 1 Then Return
        End If
        Dim PreviewFiles = DraftFiles
        Dim Info As New PackPublishDetails With {
            .PackId = PackId.Text.Trim(), .Name = PackName.Text.Trim(), .ReleaseId = PublishReleaseId.Text.Trim(),
            .Channel = PublishChannel.Text.Trim(), .Notes = PublishNotes.Text, .AllowDowngrade = PublishDowngrade.Checked, .BaselineId = Choice.Id
        }
        Dim Settings As New PackR2Settings With {
            .Account = PublishAccount.Text.Trim(), .Bucket = PublishBucket.Text.Trim(), .PublicRoot = PublishRoot.Text.Trim(),
            .AccessKey = PublishAccess.Text.Trim(), .SecretKey = PublishSecret.Password
        }
        Dim MirrorSettings = If(PublishMirror.Checked AndAlso Not ExportOnly, New PackS3MirrorSettings With {
            .Bucket = PublishMirrorBucket.Text.Trim(), .SourceAddress = PublishMirrorRoot.Text.Trim(),
            .AccessKey = PublishMirrorAccess.Text.Trim(), .SecretKey = PublishMirrorSecret.Password
        }, Nothing)
        Dim Persist = New JObject From {{"name", Info.Name}, {"id", Info.PackId}, {"channel", Info.Channel}, {"folders", PublishFolders.Text},
            {"files", PublishFilePaths.Text}, {"remember_credentials", PublishRemember.Checked}, {"baseline_id", Choice.Id}}
        CaptureStorageSettings(Persist)
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
