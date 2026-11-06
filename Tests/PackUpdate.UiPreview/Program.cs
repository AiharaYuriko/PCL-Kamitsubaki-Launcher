using System.Reflection;
using System.IO;
using System.Windows;
using System.Windows.Controls;

internal static class Program {
    [STAThread]
    static void Main(string[] args) {
        try {
            MeloongCore.Main.Init("PCL UI Preview");
            MeloongCore.Wpf.Main.Init();
            var app = (PCL.Application)Activator.CreateInstance(typeof(PCL.Application), true);
            app.InitializeComponent();
            app.Startup -= (StartupEventHandler)Delegate.CreateDelegate(typeof(StartupEventHandler), app, typeof(PCL.Application).GetMethod("Application_Startup", BindingFlags.Instance | BindingFlags.NonPublic));
            typeof(Application).GetField("_startupUri", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(app, null);
            string root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "preview-instance");
            Directory.CreateDirectory(root);
            var instance = new PCL.ModMinecraft.McInstance(root + "\\");
            var page = new PCL.PageInstancePackUpdate(instance);
            var scroll = (PCL.MyScrollViewer)typeof(PCL.PageInstancePackUpdate).GetProperty("PanBack", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).GetValue(page);
            scroll.IsVisibleChanged -= (DependencyPropertyChangedEventHandler)Delegate.CreateDelegate(typeof(DependencyPropertyChangedEventHandler), scroll, typeof(PCL.MyScrollViewer).GetMethod("MyScrollViewer_IsVisibleChanged", BindingFlags.Instance | BindingFlags.NonPublic));
            var layout = new DockPanel();
            var sidebar = new StackPanel { Width = 160, Margin = new Thickness(0, 12, 0, 0) };
            foreach (var title in new[] { "概览", "设置", "修改", "Mod 管理", "整合包更新", "导出" }) sidebar.Children.Add(new PCL.MyListItem { Title = title, Height = 36 });
            DockPanel.SetDock(sidebar, Dock.Left); layout.Children.Add(sidebar); layout.Children.Add(page);
            var window = new Window { Title = "PCL 内置页面布局验证", Content = layout, Width = 960, Height = 700, Background = (System.Windows.Media.Brush)app.Resources["ColorBrush8"] };
            // Exercise the real window and its PCL list controls with fixture data only.
            if (!args.Contains("--empty")) {
                object Field(string name) => typeof(PCL.PageInstancePackUpdate).GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).GetValue(page);
                var choices = (ComboBox)Field("Choices");
                window.ContentRendered += (_, _) => {
                    choices.Items.Add(new ComboBoxItem { Content = "神椿市搬砖中 · stable" }); choices.SelectedIndex = 0;
                    ((WrapPanel)Field("Components")).Children.Add(new PCL.MyCheckBox { Text = "必要文件", Checked = true, IsEnabled = false, Margin = new Thickness(0, 8, 18, 8) });
                    ((WrapPanel)Field("Components")).Children.Add(new PCL.MyCheckBox { Text = "画质增强", Checked = false, Margin = new Thickness(0, 8, 18, 8) });
                    ((TextBlock)Field("Status")).Text = "26h2-02 → 26h2-03\nNeoForge 21.1.218 → 21.1.219\n更新 8 项、新增 2 项、删除 1 项；下载 126 MB。\n本次更新修复任务脚本并调整模组配置。";
                    var details = (StackPanel)Field("Details");
                    foreach (var item in new[] { ("更新 / 修复 · 示例模组", "mods/example.jar"), ("新增 · 服务端任务脚本", "kubejs/server_scripts/quests.js"), ("禁用 · 不兼容模组", "mods/incompatible.jar · 与新版加载器不兼容"), ("删除 · 旧版配置", "config/obsolete.toml") }) details.Children.Add(new PCL.MyListItem { Title = item.Item1, Info = item.Item2, Height = 62, Logo = "pack://application:,,,/images/Blocks/NeoForge.png" });
                };
            }
            window.ShowDialog();
            app.Shutdown();
        } catch (Exception ex) { File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ui-error.txt"), ex.ToString()); }
    }
}
