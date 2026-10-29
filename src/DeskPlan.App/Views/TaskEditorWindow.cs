using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using DeskPlan.Core;
using DeskPlan.Infrastructure;

namespace DeskPlan.App.Views;

public sealed class TaskEditorWindow : Window
{
    public TaskEditorWindow(AppController controller, TaskOccurrence? task, string title = "")
    {
        SetResourceReference(StyleProperty, typeof(Window));
        Title = task is null ? "添加计划 · DeskPlan" : "编辑计划 · DeskPlan";
        Width = 460; Height = 720; MinWidth = 400; MinHeight = 550; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var panel = new StackPanel { Margin = new(24, 18, 24, 24) };
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        panel.Children.Add(Ui.Text(task is null ? "让今天更有方向" : "调整这项计划", 22));
        var name = new TextBox { Text = task?.Title ?? title, MaxLength = 200 };
        Ui.Field(panel, "计划名称", name);
        var date = new DatePicker { SelectedDate = task?.ScheduledDate.ToDateTime(TimeOnly.MinValue) ?? controller.ViewModel.SelectedDate,
            DisplayDateStart = new(1900, 1, 1), DisplayDateEnd = new(9998, 12, 31) };
        Ui.Field(panel, "计划日期", date);
        var timePanel = new Grid(); timePanel.ColumnDefinitions.Add(new()); timePanel.ColumnDefinitions.Add(new());
        var start = new TextBox { Text = task?.StartTime?.ToString("HH:mm") ?? "", Margin = new(0, 0, 6, 0), ToolTip = "例如 09:30，留空表示未指定时间" };
        var end = new TextBox { Text = task?.EndTime?.ToString("HH:mm") ?? "", Margin = new(6, 0, 0, 0), ToolTip = "可选，例如 10:30" };
        Grid.SetColumn(end, 1); timePanel.Children.Add(start); timePanel.Children.Add(end);
        Ui.Field(panel, "开始 / 结束时间（HH:mm，可留空）", timePanel);
        var definition = task is null ? null : controller.Store.Definitions().Single(d => d.Id == task.DefinitionId);
        var repeat = Ui.Combo("不重复", "每天", "工作日（周一至周五）", "每周（所选日期的星期）");
        repeat.SelectedIndex = (int)(definition?.Repeat ?? RepeatRule.None); Ui.Field(panel, "重复规则", repeat);
        var scope = Ui.Combo("仅本次", "本次及以后（保留以前的历史）");
        if (task is not null && definition?.Repeat != RepeatRule.None)
        {
            Ui.Field(panel, "修改范围", scope); repeat.IsEnabled = false;
            scope.IsEnabled = task.ScheduledDate >= DateOnly.FromDateTime(DateTime.Today) && task.ScheduledDate == task.OriginalDate;
            scope.SelectionChanged += (_, _) => { repeat.IsEnabled = scope.SelectedIndex == 1; date.IsEnabled = scope.SelectedIndex != 1; };
        }
        var note = new TextBox { Text = task?.Note ?? "", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 95, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxLength = 20000 };
        Ui.Field(panel, "备注", note);
        var reminder = new CheckBox { Content = "开启本地到点提醒", IsChecked = task?.ReminderEnabled ?? false, Margin = new(0, 12, 0, 5) };
        panel.Children.Add(reminder);
        var advance = new TextBox { Text = (task?.AdvanceMinutes ?? 0).ToString(), Width = 80, HorizontalAlignment = HorizontalAlignment.Left };
        Ui.Field(panel, "提前几分钟（0 为到点提醒，最多 1440）", advance);
        if (task is not null)
        {
            var label = $"当前进度 {task.Progress}%" + (task.CompletedAt is { } completed ? $" · 完成于 {completed.LocalDateTime:yyyy-MM-dd HH:mm}" : "");
            var status = Ui.Text(label, 11, "Muted"); status.Margin = new(0, 14, 0, 0); panel.Children.Add(status);
        }
        var error = Ui.Text("", 12, "Danger"); error.Margin = new(0, 10, 0, 7); panel.Children.Add(error);
        var actions = new WrapPanel(); panel.Children.Add(actions);
        var save = Ui.Button("保存计划", () =>
        {
            try
            {
                if (date.SelectedDate is null) throw new ArgumentException("请选择日期。");
                if (!int.TryParse(advance.Text, out var minutes)) throw new ArgumentException("提前提醒请输入整数分钟。");
                var draft = new TaskDraft(name.Text, DateOnly.FromDateTime(date.SelectedDate.Value), ParseTime(start.Text), ParseTime(end.Text), note.Text,
                    (RepeatRule)repeat.SelectedIndex, reminder.IsChecked == true, minutes);
                if (task is null) controller.Service.Add(draft); else controller.Service.Edit(task.Id, draft, scope.SelectedIndex == 1);
                DialogResult = true;
            }
            catch (Exception ex) { error.Text = ex.Message; }
        }, true);
        actions.Children.Add(save); actions.Children.Add(Ui.Button("取消", Close));
        if (task is not null)
        {
            actions.Children.Add(Ui.Button("删除本次", () =>
            {
                if (MessageBox.Show(this, "删除这次计划？其他日期的重复计划仍会保留。删除操作会记入日志。", "删除计划", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                try { controller.Service.Delete(task.Id); DialogResult = true; } catch (Exception ex) { error.Text = ex.Message; }
            }));
            var more = new WrapPanel { Margin = new(0, 12, 0, 0) }; panel.Children.Add(more);
            more.Children.Add(Ui.Button("查看变更记录", () => ShowLog(controller.Store, task.Id)));
            if (task.ReminderEnabled && !task.IsCompleted) more.Children.Add(Ui.Button("10 分钟后提醒", () => { controller.Service.Snooze(task.Id); error.Text = "已安排 10 分钟后提醒。"; }));
        }
        Loaded += (_, _) => { name.Focus(); name.CaretIndex = name.Text.Length; };
    }
    private static TimeOnly? ParseTime(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (TimeOnly.TryParseExact(text.Trim(), ["H:mm", "HH:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)) return time;
        throw new ArgumentException("时间格式应为 HH:mm，例如 09:30。");
    }
    private void ShowLog(PlannerStore store, Guid id)
    {
        var lines = store.AuditEvents().Where(a => a.OccurrenceId == id).Select(a => (a.ChangedAt, $"{a.ChangedAt.LocalDateTime:yyyy-MM-dd HH:mm:ss}  {a.Action}\n修改前：{a.Before}\n修改后：{a.After}\n"))
            .Concat(store.ProgressEvents().Where(p => p.OccurrenceId == id).Select(p => (p.ChangedAt, $"{p.ChangedAt.LocalDateTime:yyyy-MM-dd HH:mm:ss}  进度 {p.OldProgress}% → {p.NewProgress}%\n")))
            .OrderByDescending(x => x.ChangedAt).Select(x => x.Item2);
        new Window { Title = "计划变更记录", Width = 680, Height = 560, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new TextBox { Text = string.Join("\n", lines), IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new(15) } }.Show();
    }
}
