Imports PCL.Modpack
Imports System.Threading.Tasks

'页面固定绑定一个实例，后台更新不会随全局所选实例切换。
Public Class PageInstancePackUpdate
    Inherits MyPageRight

    Private ReadOnly Target As McInstance
    Private ReadOnly Updater As PackUpdater
    Private Release As JObject
    Private Preview As PackPlan
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
        Source.Text = CStr(Updater.Binding("source"))
        Status.Text = "首次使用：输入更新源地址。本次更新前会备份受影响文件，只保留最近一次备份。"
        Restore.IsEnabled = Updater.HasBackup OrElse Updater.NeedsRecovery
        If Updater.NeedsRecovery Then Status.Text = "上次更新未完成，请先点击'恢复上次更新'。恢复前暂时不能启动游戏。"
        AddHandler ReadSource.Click, Async Sub() Await LoadSource()
        AddHandler Check.Click, Async Sub() Await CheckUpdate()
        AddHandler Update.Click, Sub() StartUpdate(False)
        AddHandler Restore.Click, Sub() StartUpdate(True)
        AddHandler Choices.SelectionChanged, Sub()
                                                 Release = Nothing : Preview = Nothing : Components.Children.Clear() : Details.Children.Clear() : Update.IsEnabled = False
                                             End Sub
        AddHandler Source.TextChanged, Sub()
                                          Choices.Items.Clear() : Preview = Nothing : Update.IsEnabled = False
                                      End Sub
        AddHandler Loaded, Async Sub()
                               If Started Then Return
                               Started = True
                               If Not String.IsNullOrWhiteSpace(Source.Text) AndAlso Not Updater.NeedsRecovery Then Await LoadSource()
                           End Sub
    End Sub

    Private Sub SetBusy(Value As Boolean)
        Busy = Value : Source.IsEnabled = Not Value : Choices.IsEnabled = Not Value : Components.IsEnabled = Not Value
        ReadSource.IsEnabled = Not Value : Check.IsEnabled = Not Value
        Update.IsEnabled = Not Value AndAlso Preview IsNot Nothing
        Restore.IsEnabled = Not Value AndAlso (Updater.HasBackup OrElse Updater.NeedsRecovery)
        Bar.IsIndeterminate = Value
    End Sub
    Private Async Function LoadSource() As Task
        If Busy Then Return
        SetBusy(True)
        Try
            Dim Address = Source.Text.Trim()
            Dim Index = Await Task.Run(Function() PackUpdater.GetIndex(Address))
            Choices.Items.Clear()
            For Each Pack As JObject In Index("packs")
                For Each Channel As JObject In Pack("channels")
                    Dim Item As New MyComboBoxItem With {.Content = CStr(Pack("name")) & " · " & CStr(Channel("id")), .Tag = New JObject From {{"pack", Pack.DeepClone()}, {"channel", Channel.DeepClone()}}}
                    Choices.Items.Add(Item)
                    If CStr(Updater.Binding("pack_id")) = CStr(Pack("id")) AndAlso CStr(Updater.Binding("channel")) = CStr(Channel("id")) Then Choices.SelectedItem = Item
                Next
            Next
            If Choices.SelectedIndex < 0 AndAlso Choices.Items.Count > 0 Then Choices.SelectedIndex = 0
            Status.Text = "请选择整合包，点击'检查更新'查看本次变化。"
        Catch ex As Exception
            Status.Text = "读取失败：" & ex.Message & vbCrLf & "请检查地址后重试。"
        Finally
            SetBusy(False)
        End Try
        If Choices.SelectedItem IsNot Nothing Then Await CheckUpdate()
    End Function
    Private Function SelectedComponents() As List(Of String)
        Return Components.Children.OfType(Of MyCheckBox)().Where(Function(c) c.Checked).Select(Function(c) CStr(c.Tag)).ToList()
    End Function
    Private Async Function CheckUpdate() As Task
        If Busy OrElse Choices.SelectedItem Is Nothing Then Return
        SetBusy(True) : Preview = Nothing
        Try
            Dim Selection = CType(CType(Choices.SelectedItem, ComboBoxItem).Tag, JObject)
            Release = Await Task.Run(Function() PackUpdater.GetRelease(CType(Selection("pack"), JObject), CType(Selection("channel"), JObject)))
            LoadingComponents = True
            Dim Saved As JToken = If(Components.Children.Count = 0, Updater.State("selected"), New JArray(SelectedComponents()))
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
            Dim RuntimeMessage = Await Task.Run(Function() ModPackUpdate.CheckRuntime(Target, Release))
            Preview = Await Task.Run(Function() Updater.Plan(Release, Selected))
            Details.Children.Clear()
            For Each Change In Preview.Changes
                Dim Item As New MyListItem With {.Title = Change.Kind & " · " & Change.Name, .Info = Change.Path & If(Change.Reason = "", "", " · " & Change.Reason), .Height = 62,
                    .Logo = If(Change.Icon = "", "pack://application:,,,/images/Blocks/NeoForge.png", Change.Icon)}
                Details.Children.Add(Item)
            Next
            Dim Installed = If(CStr(Updater.State("release_id")), "未绑定")
            Status.Text = Installed & " → " & CStr(Release("release")("id")) & vbCrLf & RuntimeMessage & vbCrLf &
                $"{Preview.Changes.Count} 项文件变化，下载 {Preview.Bytes / 1024 / 1024:F1} MB。" & vbCrLf & CStr(Release("release")("notes"))
            If Updater.State("release_id") Is Nothing Then Status.Text &= vbCrLf & "首次绑定：保留未识别的额外文件。"
            Status.Text &= vbCrLf & "点击更新将应用以上变化；受管文件会替换，额外文件保留。"
            Update.Text = If(Preview.Changes.Count = 0, "更新 / 检查修复", "更新整合包")
        Catch ex As Exception
            Preview = Nothing : Status.Text = ex.Message
        Finally
            LoadingComponents = False : SetBusy(False)
        End Try
    End Function
    Private Sub StartUpdate(IsRestore As Boolean)
        If Busy OrElse (Not IsRestore AndAlso Preview Is Nothing) Then Return
        If IsRestore AndAlso MyMsgBox("恢复最近一次更新前的环境和文件？", "恢复更新", "继续", "取消") <> 1 Then Return
        Dim Plan = Preview
        Dim Address = Source.Text.Trim()
        SetBusy(True)
        Dim Worker As New RedoableWorker(
        Sub()
            Try
                ModPackUpdate.CheckIdle(Target)
                If IsRestore Then
                    Updater.Rollback()
                Else
                    Updater.Bind(Address, CStr(Plan.Release("pack")("id")), CStr(Plan.Release("release")("channel")))
                    Updater.Apply(Plan, Function() ModPackUpdate.PrepareRuntime(Target, Plan.Release),
                        Sub(Message) RunInUi(Sub() Status.Text = Message))
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
