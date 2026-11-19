Imports PCL.Modpack
Imports System.Threading.Tasks

'页面固定绑定一个实例，后台更新不会随全局所选实例切换。
Public Class PageInstancePackUpdate
    Inherits MyPageRight

    Private ReadOnly Target As McInstance
    Private ReadOnly Updater As PackUpdater
    Private Release As JObject
    Private Preview As PackPlan
    Private Mirror As PackMirror
    Private Busy As Boolean
    Private LoadingComponents As Boolean
    Private Started As Boolean
    Public ReadOnly Property InstancePath As String
        Get
            Return Target.PathVersion
        End Get
    End Property

    Public Sub New(Instance As McInstance)
        InitializeComponent()
        Target = Instance
        Updater = New PackUpdater(Instance.PathVersion)
        Dim Binding = Updater.Binding
        Dim SavedSource = CStr(Binding("source"))
        Source.Text = If(String.IsNullOrWhiteSpace(SavedSource), ModPackUpdate.DefaultPackSource, SavedSource)
        MirrorSource.Text = If(CStr(Binding("mirror_url")), "")
        PreferMirror.Checked = CBool(If(Binding("prefer_mirror"), New JValue(False)))
        PreferMirror.IsEnabled = Not String.IsNullOrWhiteSpace(MirrorSource.Text)
        Status.Text = "可以直接读取默认更新源，也可以输入服主提供的地址。"
        Restore.IsEnabled = Updater.HasBackup OrElse Updater.NeedsRecovery
        If Updater.NeedsRecovery Then Status.Text = "上次更新未完成，请先点击'恢复上次更新'。恢复前暂时不能启动游戏。"
        AddHandler ReadSource.Click, Async Sub() Await LoadSource()
        AddHandler Check.Click, Async Sub() Await LoadSource()
        AddHandler Update.Click, Sub() StartUpdate(False)
        AddHandler Restore.Click, Sub() StartUpdate(True)
        AddHandler Choices.SelectionChanged, Sub()
                                                 Release = Nothing : Preview = Nothing : Components.Children.Clear() : Details.Children.Clear() : Update.IsEnabled = False
                                             End Sub
        AddHandler Source.TextChanged, Sub() InvalidateSource()
        AddHandler MirrorSource.TextChanged, Sub()
                                                PreferMirror.IsEnabled = Not String.IsNullOrWhiteSpace(MirrorSource.Text)
                                                InvalidateSource()
                                            End Sub
        AddHandler PreferMirror.Change, Sub() InvalidateSource()
        AddHandler Loaded, Async Sub()
                               If Started Then Return
                               Started = True
                               If Not String.IsNullOrWhiteSpace(Source.Text) AndAlso Not Updater.NeedsRecovery Then Await LoadSource()
                           End Sub
    End Sub

    Private Sub InvalidateSource()
        Choices.Items.Clear()
        Mirror = Nothing
        Preview = Nothing
        Update.IsEnabled = False
    End Sub

    Private Sub SetBusy(Value As Boolean)
        Busy = Value : Source.IsEnabled = Not Value : Choices.IsEnabled = Not Value : Components.IsEnabled = Not Value
        ReadSource.IsEnabled = Not Value : Check.IsEnabled = Not Value
        MirrorSource.IsEnabled = Not Value
        PreferMirror.IsEnabled = Not Value AndAlso Not String.IsNullOrWhiteSpace(MirrorSource.Text)
        Update.IsEnabled = Not Value AndAlso Preview IsNot Nothing AndAlso Not Preview.IsApplied
        Restore.IsEnabled = Not Value AndAlso (Updater.HasBackup OrElse Updater.NeedsRecovery)
        Bar.IsIndeterminate = Value
    End Sub
    Private Async Function LoadSource() As Task
        If Busy Then Return
        Dim CurrentChoice = TryCast(Choices.SelectedItem, ComboBoxItem)
        Dim CurrentBinding = If(CurrentChoice Is Nothing, Nothing, TryCast(CurrentChoice.Tag, JObject))
        Dim Selected = If(CurrentBinding Is Nothing OrElse Components.Children.Count = 0, Nothing, New JArray(SelectedComponents()))
        Dim PackId = If(CurrentBinding Is Nothing, CStr(Updater.Binding("pack_id")), CStr(CurrentBinding("pack")("id")))
        Dim ChannelId = If(CurrentBinding Is Nothing, CStr(Updater.Binding("channel")), CStr(CurrentBinding("channel")("id")))
        Preview = Nothing
        SetBusy(True)
        Try
            Dim Address = Source.Text.Trim()
            Dim MirrorAddress = MirrorSource.Text.Trim()
            Mirror = If(MirrorAddress.Length = 0, Nothing, New PackMirror(MirrorAddress, PreferMirror.Checked))
            Dim Index = Await Task.Run(Function() PackUpdater.GetIndex(Updater.ResolveIndexAddress(Address), Mirror))
            Choices.Items.Clear()
            For Each Pack As JObject In Index("packs")
                For Each Channel As JObject In Pack("channels")
                    Dim Item As New MyComboBoxItem With {.Content = CStr(Pack("name")) & " · " & CStr(Channel("id")), .Tag = New JObject From {{"pack", Pack.DeepClone()}, {"channel", Channel.DeepClone()}}}
                    Choices.Items.Add(Item)
                    If PackId = CStr(Pack("id")) AndAlso ChannelId = CStr(Channel("id")) Then Choices.SelectedItem = Item
                Next
            Next
            If Choices.SelectedIndex < 0 AndAlso Choices.Items.Count > 0 Then Choices.SelectedIndex = 0
            Status.Text = "请选择整合包，点击'检查更新'查看本次变化。"
        Catch ex As Exception
            Status.Text = "读取失败：" & ex.Message & vbCrLf & "请检查地址后重试。"
            Return
        Finally
            SetBusy(False)
        End Try
        If Choices.SelectedItem IsNot Nothing Then
            Dim Selection = CType(CType(Choices.SelectedItem, ComboBoxItem).Tag, JObject)
            If PackId <> CStr(Selection("pack")("id")) OrElse ChannelId <> CStr(Selection("channel")("id")) Then Selected = Nothing
            Await CheckUpdate(Selected)
        End If
    End Function
    Private Function SelectedComponents() As List(Of String)
        Return Components.Children.OfType(Of MyCheckBox)().Where(Function(c) c.Checked).Select(Function(c) CStr(c.Tag)).ToList()
    End Function
    Private Async Function CheckUpdate(Optional SelectedOverride As JArray = Nothing) As Task
        If Busy OrElse Choices.SelectedItem Is Nothing Then Return
        SetBusy(True) : Preview = Nothing
        Try
            Dim Selection = CType(CType(Choices.SelectedItem, ComboBoxItem).Tag, JObject)
            Release = Await Task.Run(Function() PackUpdater.GetRelease(CType(Selection("pack"), JObject), CType(Selection("channel"), JObject), Mirror))
            LoadingComponents = True
            Dim Saved As JToken = If(SelectedOverride, If(Components.Children.Count = 0, Updater.State("selected"), New JArray(SelectedComponents())))
            Components.Children.Clear()
                For Each Component As JObject In Release("components")
                    Dim Id = CStr(Component("id"))
                    Dim Box As New MyCheckBox With {.Text = CStr(Component("name")), .Tag = Id, .Margin = New Thickness(0, 8, 18, 8), .IsEnabled = Not CBool(Component("required")),
                        .Checked = CBool(Component("required")) OrElse If(Saved Is Nothing, CBool(Component("default_selected")), Saved.Any(Function(s) CStr(s) = Id))}
                    AddHandler Box.Change, Sub()
                                              If LoadingComponents Then Return
                                              Preview = Nothing : Update.IsEnabled = False : Status.Text = "组件选择已改变，请点击'检查更新'刷新预览。"
                                          End Sub
                    Components.Children.Add(Box)
                Next
            LoadingComponents = False
            Dim Selected = SelectedComponents()
            Preview = Await Task.Run(Function() Updater.Plan(Release, Selected))
            Dim RuntimeMessage = If(Preview.IsApplied, "此版本已应用。", Await Task.Run(Function() ModPackUpdate.CheckRuntime(Target, Release)))
            Details.Children.Clear()
            Dim Rows = Preview.PreviewChanges().ToList()
            For Each Row In Rows
                Dim Change = Row.Change
                If Change.IsMod Then
                    Dim Name = If(Change.Name.StartsWith("mods/", StringComparison.OrdinalIgnoreCase),
                        If(CStr(Change.File?("id")), System.IO.Path.GetFileName(Change.Path)), Change.Name)
                    Dim Kind = If(Row.PreviousPaths.Length > 0 AndAlso Change.Kind = "新增", "更新", Change.Kind)
                    Dim Paths = String.Join("、", Row.PreviousPaths)
                    Dim Info = If(Paths = "", Change.Path, Paths & " → " & Change.Path)
                    If Change.Reason <> "" Then Info &= " · " & Change.Reason
                    Details.Children.Add(New MyLocalModItem(Name, Kind, Info, Change.Icon) With {.ToolTip = Kind & " · " & Name & vbCrLf & Info})
                    Continue For
                End If
                Dim Item As New MyListItem With {.Title = Change.Kind & " · " & Change.Name, .Info = Change.Path & If(Change.Reason = "", "", " · " & Change.Reason), .Height = 62,
                    .Logo = If(Change.Icon = "", "pack://application:,,,/images/Blocks/NeoForge.png", Change.Icon)}
                Details.Children.Add(Item)
            Next
            For Each Path In Preview.Preserved
                Details.Children.Add(New MyListItem With {.Title = "保留本地", .Info = Path, .Height = 52})
            Next
            Dim Installed = If(CStr(Updater.State("release_id")), "未绑定")
            Dim Archived = CStr(Release("schema")) = PackProtocol.ArchiveRelease
            Status.Text = Installed & " → " & CStr(Release("release")("id")) & vbCrLf & RuntimeMessage & vbCrLf &
                $"{Rows.Count} 项变动，{If(Archived, "待获取文件总大小", "下载")} {Preview.Bytes / 1024 / 1024:F1} MB。" & vbCrLf & CStr(Release("release")("notes"))
            If Preview.BaselineMatches.HasValue Then
                Dim ExpectedName = If(CStr(Release("baseline_instance")), "旧整合包")
                Status.Text &= vbCrLf & $"首次接入 · 基线实例 {ExpectedName} · 已匹配 {Preview.BaselineMatches.Value}/{CType(Release("baseline"), JArray).Count} 项文件。"
            ElseIf Updater.State("release_id") Is Nothing Then
                Status.Text &= vbCrLf & "首次绑定：保留未识别的额外文件。"
            End If
            If Not Preview.IsApplied Then Status.Text &= vbCrLf & If(Archived,
                "点击更新将应用本次发布操作。列表中的本地内容会保留。",
                "点击更新将应用以上变化；受管文件会替换，额外文件保留。")
            Update.Text = If(Preview.IsApplied, "已应用此版本", If(Preview.Changes.Count = 0 AndAlso Not Archived, "更新 / 检查修复", "更新整合包"))
        Catch ex As Exception
            Preview = Nothing : Status.Text = ex.Message
        Finally
            LoadingComponents = False : SetBusy(False)
        End Try
    End Function
    Private Sub StartUpdate(IsRestore As Boolean)
        If Busy OrElse (Not IsRestore AndAlso (Preview Is Nothing OrElse Preview.IsApplied)) Then Return
        If IsRestore AndAlso MyMsgBox("恢复最近一次更新前的环境和文件？", "恢复更新", "继续", "取消") <> 1 Then Return
        If Not IsRestore AndAlso Preview.BaselineMatches.HasValue AndAlso Preview.BaselineMatches.Value = 0 AndAlso
            MyMsgBox("当前实例没有匹配旧整合包基线的文件。请核对实例名称和上方的变更预览，再决定是否在此实例更新。",
                     "确认接入旧实例", "更新此实例", "取消") <> 1 Then Return
        Dim Plan = Preview
        Dim Address = Source.Text.Trim()
        Dim DownloadMirror = Mirror
        SetBusy(True)
        Dim Worker As New RedoableWorker(
        Sub()
            Try
                ModPackUpdate.CheckIdle(Target)
                If IsRestore Then
                    Updater.Rollback()
                Else
                    Updater.Bind(Address, CStr(Plan.Release("pack")("id")), CStr(Plan.Release("release")("channel")), DownloadMirror)
                    Updater.Apply(Plan, Function() ModPackUpdate.PrepareRuntime(Target, Plan.Release),
                        Sub(Message) RunInUi(Sub() Status.Text = Message), options:=New PackDownloadOptions With {
                            .Mirror = DownloadMirror,
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
                End If
                RunInUi(Sub() Status.Text = If(IsRestore, "已恢复，可以重新检查更新。", "更新完成，可以返回启动页启动游戏。仅保留最近一次回退备份。"))
            Catch ex As Exception
                RunInUi(Sub() Status.Text = "任务未完成：" & ex.Message & vbCrLf & If(Updater.NeedsRecovery, "请点击恢复上次更新。", "修正问题后重新检查即可重试。"))
            Finally
                Target.Config.DiscardCache()
                RunInUi(Sub()
                            Preview = Nothing
                            If Not Updater.NeedsRecovery Then
                                Dim Refreshed = New McInstance(Target.PathVersion).Load()
                                If PageInstanceLeft.Instance IsNot Nothing AndAlso PathUtils.IsSame(PageInstanceLeft.Instance.PathVersion, Target.PathVersion) Then PageInstanceLeft.Instance = Refreshed
                                If McInstanceSelected IsNot Nothing AndAlso PathUtils.IsSame(McInstanceSelected.PathVersion, Target.PathVersion) Then McInstanceSelected = Refreshed
                            End If
                            WriteIni(McFolderSelected & "PCL.ini", "InstanceCache", "")
                            LoaderFolderRun(McInstanceListLoader, McFolderSelected, LoaderFolderRunType.ForceRun, MaxDepth:=1, ExtraPath:="versions\")
                        End Sub)
            End Try
        End Sub)
        AddHandler Worker.Stopped, Sub() RunInUi(Sub() SetBusy(False))
        If McInstanceLock.TryAcquire(Worker, Target.PathVersion) Then
            Worker.Start()
        Else
            Status.Text = "此实例正在执行其他任务，请稍后重试。" : SetBusy(False)
        End If
    End Sub
End Class
