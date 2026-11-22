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
    Private DownloadControl As PackDownloadControl
    Private Busy As Boolean
    Private LoadingComponents As Boolean
    Private Started As Boolean
    Private FollowDefaultMirror As Boolean
    Private FillingDefaultMirror As Boolean
    Public ReadOnly Property InstancePath As String
        Get
            Return Target.PathVersion
        End Get
    End Property

    Public Sub New(Instance As McInstance)
        InitializeComponent()
        Target = Instance
        Updater = New PackUpdater(Instance.PathVersion, AddressOf PageInstancePackPublish.ResolveModSources)
        Dim Binding = Updater.Binding
        Dim SavedSource = CStr(Binding("source"))
        Source.Text = If(String.IsNullOrWhiteSpace(SavedSource), ModPackUpdate.DefaultPackSource, SavedSource)
        Dim DefaultMirror = PackMirror.DefaultAddressFor(Source.Text)
        Dim SavedMirror = CStr(Binding("mirror_url"))
        FollowDefaultMirror = SavedMirror Is Nothing OrElse (DefaultMirror.Length > 0 AndAlso SavedMirror = DefaultMirror)
        MirrorSource.Text = If(SavedMirror, DefaultMirror)
        PreferMirror.Checked = CBool(If(Binding("prefer_mirror"), New JValue(True)))
        PreferMirror.IsEnabled = Not String.IsNullOrWhiteSpace(MirrorSource.Text)
        Status.Text = "可以直接读取默认更新源，也可以输入服主提供的地址。"
        Restore.IsEnabled = Updater.HasBackup OrElse Updater.NeedsRecovery
        If Updater.NeedsRecovery Then Status.Text = "上次更新未完成，请先点击'恢复上次更新'。恢复前暂时不能启动游戏。"
        AddHandler ReadSource.Click, Async Sub() Await LoadSource()
        AddHandler ResetSource.Click, Async Sub() Await RestoreDefaultSource()
        AddHandler Check.Click, Async Sub() Await LoadSource()
        AddHandler Update.Click, Sub() StartUpdate(False)
        AddHandler Restore.Click, Sub() StartUpdate(True)
        AddHandler SwitchRoute.Click, Sub()
                                          If DownloadControl Is Nothing OrElse Not DownloadControl.TrySwitch() Then Return
                                          Logger.Info($"请求切换整合包下载线路：{Target.Name} · {Status.Text}")
                                          Status.Text = "正在切换下载线路……"
                                          RefreshDownloadActions()
                                      End Sub
        AddHandler CancelUpdate.Click, Sub()
                                           If DownloadControl Is Nothing OrElse Not DownloadControl.TryCancel() Then Return
                                           Status.Text = "正在取消更新，请等待当前步骤结束……"
                                           RefreshDownloadActions()
                                       End Sub
        AddHandler Choices.SelectionChanged, Sub()
                                                 Release = Nothing : Preview = Nothing : Components.Children.Clear() : Details.Children.Clear() : Update.IsEnabled = False
                                             End Sub
        AddHandler Source.TextChanged, Sub()
                                          If FollowDefaultMirror Then
                                              FillingDefaultMirror = True
                                              Try
                                                  MirrorSource.Text = PackMirror.DefaultAddressFor(Source.Text)
                                              Finally
                                                  FillingDefaultMirror = False
                                              End Try
                                          End If
                                          InvalidateSource()
                                      End Sub
        AddHandler MirrorSource.TextChanged, Sub()
                                                PreferMirror.IsEnabled = Not String.IsNullOrWhiteSpace(MirrorSource.Text)
                                                If Not FillingDefaultMirror Then
                                                    FollowDefaultMirror = False
                                                    InvalidateSource()
                                                End If
                                            End Sub
        AddHandler PreferMirror.Change,
            Sub(sender, user)
                InvalidateSource()
                If Not user Then Return
                Try
                    Dim SavedBinding = Updater.Binding
                    Dim SavedSourceAddress = CStr(SavedBinding("source"))
                    Dim MirrorAddress = MirrorSource.Text.Trim()
                    If Not String.IsNullOrEmpty(SavedSourceAddress) AndAlso SavedSourceAddress = Source.Text.Trim() AndAlso
                        If(CStr(SavedBinding("mirror_url")), PackMirror.DefaultAddressFor(SavedSourceAddress)) = MirrorAddress Then
                        Dim Route = If(MirrorAddress.Length = 0, Nothing, New PackMirror(MirrorAddress, PreferMirror.Checked))
                        SaveBoundSource(SavedBinding, SavedSourceAddress, Route)
                    End If
                Catch ex As Exception
                    Status.Text = "保存线路偏好失败：" & ex.Message
                End Try
            End Sub
        AddHandler Loaded, Async Sub()
                               If Started Then Return
                               Started = True
                               If Not String.IsNullOrWhiteSpace(Source.Text) AndAlso Not Updater.NeedsRecovery Then Await LoadSource()
                           End Sub
    End Sub

    Private Sub InvalidateSource()
        Choices.Items.Clear()
        Release = Nothing
        Components.Children.Clear()
        Details.Children.Clear()
        Mirror = Nothing
        Preview = Nothing
        Update.IsEnabled = False
    End Sub

    Private Sub SaveBoundSource(Binding As JObject, Address As String, Route As PackMirror)
        Dim PackId = CStr(Binding("pack_id"))
        Dim ChannelId = CStr(Binding("channel"))
        If String.IsNullOrEmpty(PackId) OrElse String.IsNullOrEmpty(ChannelId) Then Return
        If Address = CStr(Binding("source")) AndAlso If(Route?.Address, "") = CStr(Binding("mirror_url")) AndAlso
            (Route Is Nothing OrElse Route.PreferMirror = CBool(If(Binding("prefer_mirror"), New JValue(True)))) Then Return
        If McInstanceLock.IsLocked(Target) Then Throw New IOException("此实例正在执行其他任务，请稍后重试。")
        Updater.Bind(Address, PackId, ChannelId, Route)
    End Sub

    Private Async Function RestoreDefaultSource() As Task
        If Busy Then Return
        FollowDefaultMirror = True
        Source.Text = ModPackUpdate.DefaultPackSource
        FillingDefaultMirror = True
        Try
            MirrorSource.Text = PackMirror.DefaultAddress
        Finally
            FillingDefaultMirror = False
        End Try
        InvalidateSource()
        Try
            SaveBoundSource(Updater.Binding, Source.Text, New PackMirror(MirrorSource.Text, PreferMirror.Checked))
        Catch ex As Exception
            Status.Text = "恢复默认更新源失败：" & ex.Message
            Return
        End Try
        If Updater.NeedsRecovery Then
            Status.Text = "已恢复默认更新源。请先恢复上次更新，再重新检查。"
        Else
            Await LoadSource()
        End If
    End Function

    Private Sub SetBusy(Value As Boolean)
        Busy = Value : Source.IsEnabled = Not Value : Choices.IsEnabled = Not Value : Components.IsEnabled = Not Value
        ReadSource.IsEnabled = Not Value : ResetSource.IsEnabled = Not Value : Check.IsEnabled = Not Value
        MirrorSource.IsEnabled = Not Value
        PreferMirror.IsEnabled = Not Value AndAlso Not String.IsNullOrWhiteSpace(MirrorSource.Text)
        Update.IsEnabled = Not Value AndAlso Preview IsNot Nothing AndAlso Not Preview.IsApplied
        Restore.IsEnabled = Not Value AndAlso (Updater.HasBackup OrElse Updater.NeedsRecovery)
        Bar.IsIndeterminate = Value
        Bar.Value = 0
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
        Dim IconRows As New List(Of (Item As MyLocalModItem, Sources As JArray))
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
            Dim Binding = Updater.Binding
            If CStr(Binding("pack_id")) = CStr(Release("pack")("id")) AndAlso
                CStr(Binding("channel")) = CStr(Release("release")("channel")) Then
                SaveBoundSource(Binding, Source.Text.Trim(), Mirror)
            End If
            If Preview.IsCurrentArchiveRelease Then
                LoadingComponents = True
                For Each Box In Components.Children.OfType(Of MyCheckBox)()
                    Box.Checked = Preview.Selected.Contains(CStr(Box.Tag))
                    Box.IsEnabled = False
                Next
                LoadingComponents = False
            End If
            Dim RuntimeMessage = If(Preview.IsApplied, "此版本已应用。", Await Task.Run(Function() ModPackUpdate.CheckRuntime(Target, Release)))
            Details.Children.Clear()
            Dim Rows = Preview.PreviewChanges().ToList()
            For Each Row In Rows
                Dim Change = Row.Change
                Dim DownloadSource = String.Join(" / ", Change.DownloadSources)
                Dim SourceDescription = If(DownloadSource = "", "", "下载来源：" & DownloadSource)
                If Change.IsMod Then
                    Dim Name = If(Change.Name.StartsWith("mods/", StringComparison.OrdinalIgnoreCase),
                        If(CStr(Change.File?("id")), If(CStr(Change.File?("fabric_id")), System.IO.Path.GetFileName(Change.Path))), Change.Name)
                    Dim Kind = If(Row.PreviousPaths.Length > 0 AndAlso Change.Kind = "新增", "更新", Change.Kind)
                    Dim Paths = String.Join("、", Row.PreviousPaths)
                    Dim Info = If(Paths = "", Change.Path, Paths & " → " & Change.Path)
                    If Change.Reason <> "" Then Info &= " · " & Change.Reason
                    Dim ModItem As New MyLocalModItem(Name, Kind, Info, Change.Icon, DownloadSource) With {
                        .ToolTip = Kind & " · " & Name & vbCrLf & Info & If(SourceDescription = "", "", vbCrLf & SourceDescription)}
                    Details.Children.Add(ModItem)
                    If Change.Icon = "" AndAlso Change.ModSources IsNot Nothing Then IconRows.Add((ModItem, Change.ModSources))
                    Continue For
                End If
                Dim Item As New MyListItem With {.Title = Change.Kind & " · " & Change.Name,
                    .Info = Change.Path & If(Change.Reason = "", "", " · " & Change.Reason) & If(SourceDescription = "", "", " · " & SourceDescription),
                    .ToolTip = If(SourceDescription = "", Nothing, SourceDescription), .Height = 62,
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
            If Not Preview.IsApplied Then Status.Text &= vbCrLf & If(Preview.IsCurrentArchiveRelease,
                "点击修复模组将按预览补装或替换模组。",
                If(Archived, "点击更新将按预览安装、替换或移除文件。",
                   "点击更新将应用以上变化；受管文件会替换，额外文件保留。"))
            Update.Text = If(Preview.IsApplied, "已应用此版本",
                If(Preview.IsCurrentArchiveRelease, "修复模组", If(Preview.Changes.Count = 0 AndAlso Not Archived, "更新 / 检查修复", "更新整合包")))
        Catch ex As Exception
            Preview = Nothing : Status.Text = ex.Message
        Finally
            LoadingComponents = False : SetBusy(False)
        End Try
        If Preview IsNot Nothing AndAlso IconRows.Count > 0 Then Await LoadPreviewIcons(Preview, IconRows)
    End Function

    Private Async Function LoadPreviewIcons(Plan As PackPlan, Rows As List(Of (Item As MyLocalModItem, Sources As JArray))) As Task
        Dim Pending As New HashSet(Of MyLocalModItem)(Rows.Select(Function(Row) Row.Item))
        For Each Platform In {ResourcePlatforms.Modrinth, ResourcePlatforms.CurseForge}
            Dim PlatformName = If(Platform = ResourcePlatforms.Modrinth, "modrinth", "curseforge")
            Dim IdPattern = If(Platform = ResourcePlatforms.Modrinth, "\A[A-Za-z0-9]{1,32}\z", "\A[1-9][0-9]{0,9}\z")
            Dim ItemsByProject = (
                From Row In Rows
                Where Pending.Contains(Row.Item)
                From Source In Row.Sources.OfType(Of JObject)()
                Where Source.Value(Of String)("platform") = PlatformName
                Let Id = Source.Value(Of String)("project")
                Where Id IsNot Nothing AndAlso Id.RegexCheck(IdPattern)
                Select Id, Row.Item).ToLookup(Function(Entry) Entry.Id, Function(Entry) Entry.Item, StringComparer.Ordinal)
            Dim ProjectIds = ItemsByProject.Select(Function(Group) Group.Key).ToArray()
            Const BatchSize As Integer = 50
            For Start As Integer = 0 To ProjectIds.Length - 1 Step BatchSize
                If Preview IsNot Plan Then Return
                Dim Batch = ProjectIds.Skip(Start).Take(BatchSize).ToArray()
                Dim Projects As List(Of ResourceProject)
                Try
                    Projects = Await Task.Run(Function() ResourceProject.FromProjectIds(Batch, Platform).ToList())
                Catch ex As Exception
                    Logger.Warn(ex, "获取整合包模组图标失败")
                    Exit For
                End Try
                If Preview IsNot Plan Then Return
                For Each Project In Projects
                    If Project.Platform <> Platform OrElse String.IsNullOrEmpty(Project.LogoUrl) Then Continue For
                    For Each Item In ItemsByProject(Project.Id)
                        If Pending.Remove(Item) Then Item.SetPreviewProject(Project)
                    Next
                Next
            Next
        Next
    End Function
    Private Sub RefreshDownloadActions()
        DownloadActions.Visibility = If(DownloadControl Is Nothing, Visibility.Collapsed, Visibility.Visible)
        SwitchRoute.IsEnabled = DownloadControl IsNot Nothing AndAlso DownloadControl.CanSwitch
        CancelUpdate.IsEnabled = DownloadControl IsNot Nothing AndAlso DownloadControl.CanCancel
    End Sub

    Private Sub StartUpdate(IsRestore As Boolean)
        If Busy OrElse (Not IsRestore AndAlso (Preview Is Nothing OrElse Preview.IsApplied)) Then Return
        If IsRestore AndAlso MyMsgBox("恢复最近一次更新前的环境和文件？", "恢复更新", "继续", "取消") <> 1 Then Return
        If Not IsRestore AndAlso Preview.BaselineMatches.HasValue AndAlso Preview.BaselineMatches.Value = 0 AndAlso
            MyMsgBox("当前实例没有匹配旧整合包基线的文件。请核对实例名称和上方的变更预览，再决定是否在此实例更新。",
                     "确认接入旧实例", "更新此实例", "取消") <> 1 Then Return
        Dim Plan = Preview
        Dim Address = Source.Text.Trim()
        Dim DownloadMirror = Mirror
        Dim Control = If(IsRestore, Nothing, New PackDownloadControl())
        DownloadControl = Control
        RefreshDownloadActions()
        SetBusy(True)
        Status.Text = If(IsRestore, "正在恢复上次更新……", "正在准备更新……")
        If Not ModPackUpdate.StartPackTask(Target, Updater,
            Function(Progress)
                If IsRestore Then
                    Updater.Rollback()
                    Return "已恢复，可以重新检查更新。"
                End If
                Control.ThrowIfCanceled()
                Updater.Bind(Address, CStr(Plan.Release("pack")("id")), CStr(Plan.Release("release")("channel")), DownloadMirror)
                ModPackUpdate.ApplyPackUpdate(Target, Updater, Plan, DownloadMirror, Progress, Control,
                    Sub(Snapshot) RunInUi(
                        Sub()
                            If Control.IsCancellationRequested Then Return
                            Status.Text = Snapshot.Text
                            Bar.IsIndeterminate = Not Snapshot.Fraction.HasValue
                            Bar.Value = Snapshot.Fraction.GetValueOrDefault() * Bar.Maximum
                            RefreshDownloadActions()
                        End Sub))
                Return "更新完成，可以返回启动页启动游戏。仅保留最近一次回退备份。"
            End Function,
            Sub(Message, Kind)
                Status.Text = Message
                DownloadControl = Nothing
                RefreshDownloadActions()
            End Sub,
            Sub(Text)
                If Control?.IsCancellationRequested = True Then Return
                Status.Text = Text
                Bar.IsIndeterminate = True
                Bar.Value = 0
                RefreshDownloadActions()
            End Sub,
            Sub()
                Preview = Nothing
                SetBusy(False)
            End Sub) Then SetBusy(False)
    End Sub
End Class
