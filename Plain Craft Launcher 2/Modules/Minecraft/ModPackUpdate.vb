Imports PCL.Modpack

Public Module ModPackUpdate
    Public Sub CheckIdle(Instance As McInstance)
        If McLaunchLoader.State = LoadState.Loading OrElse HasRunningMinecraft Then Throw New Exception("请先关闭正在运行的游戏，再更新整合包。")
        If Not PathUtils.IsSame(Instance.PathIndie, Instance.PathVersion) Then Throw New Exception("请先为此实例启用版本隔离，并将整合包放入实例目录。")
        For Each Native In DirectoryUtils.EnumerateFiles(GetNativesFolder(Instance), includeSubDirectories:=True, searchPattern:="*.dll")
            If Not FileUtils.IsAvailable(Native) Then Throw New Exception("游戏仍在使用文件，请关闭游戏后重试。")
        Next
    End Sub
    Private Function Current(Instance As McInstance) As McComponents
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
        Dim Existing = Current(Instance)
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
        Dim Existing = Current(Instance)
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
