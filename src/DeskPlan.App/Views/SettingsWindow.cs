using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using DeskPlan.Core;
using DeskPlan.Infrastructure;
using DeskPlan.Windows;
using Microsoft.Win32;

namespace DeskPlan.App.Views;

public sealed class SettingsWindow : Window
{
    public SettingsWindow(AppController controller)
    {
        SetResourceReference(StyleProperty, typeof(Window));
        Title = "设置与备份 · DeskPlan"; Width = 470; Height = 740; MinWidth = 420; MinHeight = 560; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var panel = new StackPanel { Margin = new(26, 22, 26, 25) };
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        panel.Children.Add(Ui.Text("按自己的节奏", 23));
        var mode = Ui.Combo("桌面壁纸层", "悬浮置顶"); mode.SelectedIndex = (int)controller.Settings.Mode; Ui.Field(panel, "显示模式", mode);
        var theme = Ui.Combo("浅色 · 晨光", "深色 · 夜航", "半透明 · 玻璃"); theme.SelectedIndex = (int)controller.Settings.Theme; Ui.Field(panel, "主题", theme);
        var opacity = new Slider { Minimum = 35, Maximum = 100, Value = controller.Settings.BackgroundOpacity * 100 };
        var opacityLabel = Ui.Text("", 11, "Muted");
        opacity.ValueChanged += (_, _) => opacityLabel.Text = $"背景不透明度 {opacity.Value:0}%"; opacityLabel.Text = $"背景不透明度 {opacity.Value:0}%";
        Ui.Field(panel, "背景透明度（文字保持清晰）", opacity); panel.Children.Add(opacityLabel);
        var font = new Slider { Minimum = 11, Maximum = 20, Value = controller.Settings.FontSize };
        Ui.Field(panel, "任务字体大小", font);
        var close = new CheckBox { Content = "关闭组件时隐藏到系统托盘", IsChecked = controller.Settings.CloseToTray, Margin = new(0, 14, 0, 4) };
        var startup = new CheckBox { Content = "登录 Windows 后自动启动", IsChecked = StartupRegistration.IsEnabled() };
        var manual = new CheckBox { Content = "按手动顺序排列（也可拖动任务左上角）", IsChecked = controller.Settings.ManualSort };
        var completed = new CheckBox { Content = "折叠已完成计划", IsChecked = controller.Settings.CollapseCompleted };
        panel.Children.Add(close); panel.Children.Add(startup); panel.Children.Add(manual); panel.Children.Add(completed);
        var error = Ui.Text("", 11, "Danger"); error.Margin = new(0, 10, 0, 10); panel.Children.Add(error);
        panel.Children.Add(Ui.Button("保存设置", () =>
        {
            try
            {
                controller.ApplySettings(controller.Settings with { Mode = (DisplayMode)mode.SelectedIndex, Theme = (AppTheme)theme.SelectedIndex,
                    BackgroundOpacity = opacity.Value / 100, FontSize = font.Value, CloseToTray = close.IsChecked == true,
                    Autostart = startup.IsChecked == true, ManualSort = manual.IsChecked == true, CollapseCompleted = completed.IsChecked == true });
                error.Text = "设置已保存。";
            }
            catch (Exception ex) { error.Text = ex.Message; }
        }, true));
        var backups = new WrapPanel { Margin = new(0, 24, 0, 12) }; panel.Children.Add(backups);
        backups.Children.Add(Ui.Button("导出 JSON 备份", () =>
        {
            try
            {
                controller.FlushAll();
                var dialog = new SaveFileDialog { Filter = "DeskPlan 备份 (*.json)|*.json", FileName = $"DeskPlan-{DateTime.Now:yyyyMMdd-HHmm}.json" };
                if (dialog.ShowDialog(this) == true) { BackupService.Export(controller.Store, dialog.FileName); error.Text = "完整备份已导出。"; }
            }
            catch (Exception ex) { error.Text = ex.Message; }
        }));
        backups.Children.Add(Ui.Button("从备份恢复", () =>
        {
            try
            {
                var dialog = new OpenFileDialog { Filter = "DeskPlan 备份 (*.json)|*.json" };
                if (dialog.ShowDialog(this) != true) return;
                var backup = BackupService.Read(dialog.FileName);
                if (MessageBox.Show(this, $"备份时间：{backup.ExportedAt.LocalDateTime:yyyy-MM-dd HH:mm}\n包含 {backup.Occurrences.Count(t => !t.Deleted)} 条计划、{backup.Reviews.Count} 条总结。\n\n恢复将替换当前数据，操作前会自动保存当前数据库备份。是否继续？",
                    "确认恢复", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                controller.FlushAll(); var safety = controller.Store.Restore(backup); controller.ReloadAfterRestore();
                MessageBox.Show(this, "恢复完成。原数据库备份保存在：\n" + safety, "DeskPlan"); Close();
            }
            catch (Exception ex) { error.Text = ex.Message; }
        }));
        panel.Children.Add(Ui.Text("所有计划仅保存在这台电脑。建议定期把 JSON 备份复制到其他磁盘。", 11, "Muted"));
        var folder = System.IO.Path.GetDirectoryName(controller.Store.DatabasePath)!;
        Ui.Field(panel, "数据目录", new TextBox { Text = folder, IsReadOnly = true, TextWrapping = TextWrapping.Wrap });
        var openFolder = Ui.Button("打开数据目录", () => Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true })); openFolder.Margin = new(0, 8, 0, 18); panel.Children.Add(openFolder);
        var version = typeof(SettingsWindow).Assembly.GetName().Version?.ToString(3) ?? "未知版本";
        panel.Children.Add(Ui.Text($"DeskPlan {version}  ·  离线桌面计划\n桌面嵌入不可用时自动回退到普通窗口，可从托盘重新显示。通知是否展示由 Windows 通知设置与勿扰模式决定。", 11, "Muted"));
    }
}
