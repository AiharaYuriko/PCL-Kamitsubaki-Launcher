Imports PCL.Modpack
Imports System.Threading.Tasks

Public Module ModPackUpdate
    Public Const DefaultPackSource As String = PackMirror.DefaultPrimaryAddress
    Private StartupCheckStarted As Boolean

    Friend Async Function CheckStartupUpdate(Selection As Task(Of McInstance)) As Task
        If StartupCheckStarted Then Return
        StartupCheckStarted = True
        Dim Instance = Await Selection
        If Instance Is Nothing Then Return
        Dim Release As JObject
        Dim FirstConnection As Boolean = False
        Try
            Dim Updater As New PackUpdater(Instance.PathVersion)
            Release = Await Task.Run(Function() Updater.CheckForUpdate())
            If Release Is Nothing AndAlso PathUtils.IsSame(Instance.PathIndie, Instance.PathVersion) Then
                Release = Await Task.Run(Function() Updater.CheckLegacyStartupUpdate(Paths.Base))
                FirstConnection = Release IsNot Nothing
            End If
        Catch ex As Exception
            Logger.Warn(ex, $"检查整合包更新失败：{Instance.Name}")
            Hint($"{Instance.Name} 的整合包更新检查失败，请在整合包更新页重试。", HintType.Red)
            Return
        End Try
        If Release Is Nothing Then Return

        Dim ReleaseInfo = Release("release")
        Dim ReleaseId = CStr(ReleaseInfo("id"))
        Dim ReleaseKey As String = New JArray(Release("pack")("id"), ReleaseInfo("channel"), ReleaseInfo("id"),
                                              ReleaseInfo("sequence")).ToString(Newtonsoft.Json.Formatting.None)
        Dim Message = If(FirstConnection, $"{Instance.Name} 可接入 PCL 整合包更新，当前发布为 {ReleaseId}。",
                         $"{Instance.Name} 有整合包更新（{ReleaseId}）。")
        If Settings.Get(Of String)("VersionPackUpdateDismissedRelease", Instance:=Instance) = ReleaseKey Then
            Hint(Message)
            Return
        End If
        Dim Quiet As Boolean
        Dim Choice As Integer
        If FirstConnection Then
            Choice = MyMsgBox(Message & vbCrLf & "请先查看变更预览并确认组件选择。", "整合包更新",
                              "查看更新详情", "稍后", CheckBoxText:="此更新版本不再弹窗", CheckBoxChecked:=Quiet)
        Else
            Choice = MyMsgBox(Message & vbCrLf & "一键更新将沿用此实例已保存的组件选择。", "整合包更新",
                              "查看更新详情", "一键更新", "稍后", CheckBoxText:="此更新版本不再弹窗", CheckBoxChecked:=Quiet,
                              HighlightButton:=2)
        End If
        If Quiet Then Settings.Set("VersionPackUpdateDismissedRelease", ReleaseKey, Instance:=Instance)
        Select Case Choice
            Case 1
                If McInstanceLock.IsLocked(Instance) Then
                    Hint("该版本正在被修改，请稍后进入整合包更新页。", HintType.Red)
                    Return
                End If
                If FrmMain.PageCurrent = FormMain.PageType.InstanceSetup AndAlso PageInstanceLeft.Instance IsNot Nothing AndAlso
                    Not PathUtils.IsSame(PageInstanceLeft.Instance.PathVersion, Instance.PathVersion) Then
                    FrmMain.PageChange(FormMain.PageType.Launch)
                End If
                Instance.Load()
                PageInstanceLeft.Instance = Instance
                FrmMain.PageChange(FormMain.PageType.InstanceSetup, FormMain.PageSubType.InstancePackUpdate)
            Case 2
                If Not FirstConnection Then
                    StartQuickUpdate(Instance, Release)
                ElseIf Quiet Then
                    Hint(Message)
                End If
            Case 3
                If Quiet Then Hint(Message)
        End Select
    End Function

    Private Sub StartQuickUpdate(Instance As McInstance, OfferedRelease As JObject)
        Dim Updater As New PackUpdater(Instance.PathVersion, AddressOf PageInstancePackPublish.ResolveModSources)
        If StartPackTask(Instance, Updater,
            Function(Progress)
                Dim Binding = Updater.Binding
                Dim Release = Updater.CheckForUpdate()
                If Release Is Nothing OrElse Not JToken.DeepEquals(OfferedRelease, Release) OrElse Not JToken.DeepEquals(Binding, Updater.Binding) Then
                    Throw New IOException("更新信息已变更，请到整合包更新页重新检查。")
                End If
                Dim Saved = TryCast(Updater.State("selected"), JArray)
                Dim Selected = If(Saved IsNot Nothing, Saved.Values(Of String)(),
                                  Release("components").Where(Function(Component) CBool(Component("default_selected"))).
                                      Select(Function(Component) CStr(Component("id"))))
                Dim Plan = Updater.Plan(Release, Selected)
                If Plan.BaselineMatches.HasValue AndAlso Plan.BaselineMatches.Value = 0 Then
                    Throw New IOException("当前实例没有匹配旧版基线的文件，请到整合包更新页核对变更预览。")
                End If
                ApplyPackUpdate(Instance, Updater, Plan, PackUpdater.GetBoundMirror(Binding), Progress)
                Return $"{Instance.Name} 的整合包已更新。"
            End Function, Sub(Message, Kind) Hint(Message, Kind)) Then
            Hint($"正在更新 {Instance.Name} 的整合包…")
        End If
    End Sub

    Friend Function StartPackTask(Instance As McInstance, Updater As PackUpdater, Work As Func(Of Action(Of String), String),
                                  ReportResult As Action(Of String, HintType), Optional ReportProgress As Action(Of String) = Nothing,
                                  Optional Finished As Action = Nothing) As Boolean
        Dim Worker As New RedoableWorker(
            Sub()
                Try
                    CheckIdle(Instance)
                    Dim Message = Work(Sub(Text)
                                           If ReportProgress IsNot Nothing Then RunInUi(Sub() ReportProgress(Text))
                                       End Sub)
                    RunInUi(Sub() ReportResult(Message, HintType.Green))
                Catch ex As Exception
                    Logger.Warn(ex, $"整合包任务未完成：{Instance.Name}")
                    Dim Message = "任务未完成：" & ex.Message & vbCrLf &
                        If(Updater.NeedsRecovery, "请点击恢复上次更新。", "修正问题后重新检查即可重试。")
                    RunInUi(Sub() ReportResult(Message, HintType.Red))
                Finally
                    Instance.Config.DiscardCache()
                    RunInUi(Sub()
                                If Not Updater.NeedsRecovery Then
                                    Dim Refreshed = New McInstance(Instance.PathVersion).Load()
                                    If PageInstanceLeft.Instance IsNot Nothing AndAlso PathUtils.IsSame(PageInstanceLeft.Instance.PathVersion, Instance.PathVersion) Then PageInstanceLeft.Instance = Refreshed
                                    If McInstanceSelected IsNot Nothing AndAlso PathUtils.IsSame(McInstanceSelected.PathVersion, Instance.PathVersion) Then McInstanceSelected = Refreshed
                                End If
                                WriteIni(McFolderSelected & "PCL.ini", "InstanceCache", "")
                                LoaderFolderRun(McInstanceListLoader, McFolderSelected, LoaderFolderRunType.ForceRun, MaxDepth:=1, ExtraPath:="versions\")
                            End Sub)
                End Try
            End Sub)
        If Finished IsNot Nothing Then AddHandler Worker.Stopped, Sub() RunInUi(Finished)
        If McInstanceLock.TryAcquire(Worker, Instance.PathVersion) Then
            Worker.Start()
            Return True
        End If
        ReportResult("此实例正在执行其他任务，请稍后重试。", HintType.Red)
        Return False
    End Function

    Friend Sub ApplyPackUpdate(Instance As McInstance, Updater As PackUpdater, Plan As PackPlan, Mirror As PackMirror, Progress As Action(Of String))
        Updater.Apply(Plan, Function() PrepareRuntime(Instance, Plan.Release), Progress, options:=New PackDownloadOptions With {
            .Mirror = Mirror,
            .ApproveWholeArchive = Function(Size) RunInUiWait(Function() MyMsgBox(
                $"下载地址暂时无法读取 ZIP 分段。是否改为下载完整 ZIP（{Size / 1048576.0:F1} MB）？",
                "下载完整整合包", "下载", "取消") = 1),
            .SelectLocalFile = Function(Path, Failure) RunInUiWait(
                Function()
                    If MyMsgBox("下载失败：" & Path & vbCrLf & Failure & vbCrLf & "可选择本地的同版本文件继续。",
                                "获取整合包文件", "选择文件", "取消") <> 1 Then Return Nothing
                    Return Dialogs.SelectFile("选择 " & Path, False).FirstOrDefault()
                End Function),
            .SkipOptional = Function(Path) RunInUiWait(Function() MyMsgBox("跳过未取得的可选文件？" & vbCrLf & Path,
                "可选文件下载失败", "跳过", "停止更新") = 1)
        })
    End Sub

    Public Sub CheckIdle(Instance As McInstance)
        If McLaunchLoader.State = LoadState.Loading OrElse HasRunningMinecraft Then Throw New Exception("请先关闭正在运行的游戏，再更新整合包。")
        If Not PathUtils.IsSame(Instance.PathIndie, Instance.PathVersion) Then Throw New Exception("请先为此实例启用版本隔离，并将整合包放入实例目录。")
        For Each Native In DirectoryUtils.EnumerateFiles(GetNativesFolder(Instance), includeSubDirectories:=True, searchPattern:="*.dll")
            If Not FileUtils.IsAvailable(Native) Then Throw New Exception("游戏仍在使用文件，请关闭游戏后重试。")
        Next
    End Sub
    Public Function ReadPackRuntime(Instance As McInstance) As McComponents
        Instance.Config.DiscardCache()
        Dim Manifest = Configs.McInstallManifest.Get(Instance.Config)
        Dim Result = If(Manifest IsNot Nothing, Manifest.Components, Nothing)
        If Result Is Nothing Then
            Dim Info = New McInstance(Instance.PathVersion).Version
            If Not Info.Reliable Then Throw New Exception("无法可靠识别此实例，请先使用 PCL 的修改版本功能检查它。")
            Result = New McComponents(Info.VanillaName, Info.ModLoaderVersions)
        End If
        If Result.ModLoaderVersions.Count <> 1 OrElse Not Result.ModLoaderVersions.ContainsKey(ModLoaders.NeoForge) OrElse Result.OptiFineEntry IsNot Nothing Then Throw New Exception("第一版自动更新支持独立的 NeoForge 实例；其他加载器请使用独立实例。")
        Return Result
    End Function
    Private Function FindEntry(Release As JObject) As DlNeoForgeListEntry
        DlNeoForgeListLoader.WaitForExit()
        Dim Entry = DlNeoForgeListLoader.Output.Value.FirstOrDefault(Function(e) e.VersionName = CStr(Release("runtime")("loader")("version")) AndAlso e.Inherit = CStr(Release("runtime")("minecraft")))
        If Entry Is Nothing Then Throw New Exception("PCL 版本列表中没有此 Minecraft 对应的目标 NeoForge，请联系发布者核对版本。")
        Return Entry
    End Function
    Public Function CheckRuntime(Instance As McInstance, Release As JObject) As String
        CheckIdle(Instance)
        Dim Existing = ReadPackRuntime(Instance)
        If Existing.MinecraftVersion <> CStr(Release("runtime")("minecraft")) Then Throw New Exception("此更新需要 Minecraft " & CStr(Release("runtime")("minecraft")) & "。请在下载页面安装独立实例后绑定更新源。")
        Dim OldVersion = Existing.ModLoaderVersions(ModLoaders.NeoForge)
        Dim NewVersion = CStr(Release("runtime")("loader")("version"))
        If OldVersion = NewVersion Then Return "NeoForge " & OldVersion & " 已符合要求。"
        Dim Entry = FindEntry(Release)
        If New DlNeoForgeListEntry(OldVersion).Version > Entry.Version AndAlso Not CBool(Release("runtime")("allow_loader_downgrade")) Then Throw New Exception("清单未允许降低 NeoForge 版本，已停止更新。")
        Return "NeoForge " & OldVersion & " → " & NewVersion
    End Function
    Public Function PrepareRuntime(Instance As McInstance, Release As JObject) As PackRuntime
        CheckRuntime(Instance, Release)
        Dim Existing = ReadPackRuntime(Instance)
        Dim NewVersion = CStr(Release("runtime")("loader")("version"))
        Dim Runtime As New PackRuntime
        If Existing.ModLoaderVersions(ModLoaders.NeoForge) = NewVersion Then Return Runtime
        Dim BuildFolder = RequestTaskTempFolder() & Instance.Name & "\"
        Dim Request As New McInstallRequest With {
            .NewInstanceName = Instance.Name, .InstanceFolder = BuildFolder, .IsIntermediateOutput = True,
            .Components = New McComponents(Existing.MinecraftVersion, New Dictionary(Of ModLoaders, String) From {{ModLoaders.NeoForge, NewVersion}}),
            .NeoForgeEntry = FindEntry(Release)
        }
        Dim Loader As New LoaderCombo(Of String)(Instance.Name & " 准备 NeoForge", McInstallLoadersGet(Request))
        Loader.WaitForExit(BuildFolder)
        Dim NewManifest = Configs.McInstallManifest.Get(New McInstance(BuildFolder).Config)
        If NewManifest Is Nothing Then Throw New Exception("临时环境缺少安装记录，未修改当前实例。")
        Dim OldManifest = Configs.McInstallManifest.Get(Instance.Config)
        Dim Root = Path.GetFullPath(Instance.PathVersion).TrimEnd("\"c) & "\"
        Dim AddPath As Action(Of String) =
            Sub(Value)
                Dim Full = Path.GetFullPath(Value)
                If Not Full.StartsWith(Root, StringComparison.OrdinalIgnoreCase) Then Throw New Exception("运行环境需要修改实例目录之外的文件，已停止。")
                Dim Relative = Full.Substring(Root.Length).Replace("\", "/")
                PackProtocol.Safe(Root, Relative, True)
                Runtime.Paths.Add(Relative)
            End Sub
        AddPath(Instance.GetJsonPath())
        AddPath(Path.ChangeExtension(Instance.GetJsonPath(), ".jar"))
        AddPath(Instance.PathVersion & Instance.Name & ".jar")
        AddPath(Instance.PathVersion & "PCL\config.json")
        For Each File In NewManifest.ManagedFiles
            AddPath(Path.Combine(Instance.PathIndie, File.RelativePath))
        Next
        If OldManifest IsNot Nothing Then
            For Each File In OldManifest.ManagedFiles.Where(Function(f) f.Component IsNot Nothing)
                AddPath(Path.Combine(Instance.PathIndie, File.RelativePath))
            Next
        End If
        If Directory.Exists(BuildFolder & "resources") Then Throw New Exception("此实例的旧资源布局暂不支持自动整合包更新。")
        Runtime.Commit = Sub() McInstallCommit(Instance, NewManifest, BuildFolder, Instance.PathIndie, PreservePackFiles:=True)
        Runtime.Verify = Sub()
                             Instance.Config.DiscardCache()
                             Dim Actual = New McInstance(Instance.PathVersion).Version
                             If Actual.VanillaName <> Existing.MinecraftVersion OrElse Not Actual.ModLoaderVersions.ContainsKey(ModLoaders.NeoForge) OrElse Actual.ModLoaderVersions(ModLoaders.NeoForge) <> NewVersion Then Throw New Exception("更新后的 NeoForge 环境校验失败。")
                         End Sub
        Return Runtime
    End Function
End Module
