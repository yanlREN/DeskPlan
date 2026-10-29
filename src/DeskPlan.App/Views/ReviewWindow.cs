using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using DeskPlan.Core;
using DeskPlan.Infrastructure;
using Microsoft.Win32;

namespace DeskPlan.App.Views;

public sealed class ReviewWindow : Window
{
    private readonly AppController controller;
    private readonly bool overdue;
    private readonly ComboBox period = Ui.Combo("每日", "每周", "每月");
    private readonly DatePicker date = new() { SelectedDate = DateTime.Today, Width = 150, DisplayDateStart = new(1900, 1, 1), DisplayDateEnd = new(9998, 12, 31) };
    private readonly StackPanel body = new();
    private readonly TextBox note = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 100, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBlock heading = Ui.Text("", 22);
    private readonly TextBlock saved = Ui.Text("", 11, "Muted");
    private readonly DispatcherTimer noteTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private ReviewPeriod displayedPeriod;
    private DateOnly displayedStart;
    private bool dirty, rendering, loadingNote;
    private readonly StackPanel noteArea = new();
    public ReviewWindow(AppController controller, bool overdue = false)
    {
        this.controller = controller; this.overdue = overdue;
        SetResourceReference(StyleProperty, typeof(Window));
        Title = overdue ? "待处理历史计划 · DeskPlan" : "历史回顾 · DeskPlan";
        Width = 960; Height = 780; MinWidth = 720; MinHeight = 600; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var root = new DockPanel { Margin = new(28, 22, 28, 22) }; Content = root;
        var header = new StackPanel(); DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        header.Children.Add(Ui.Text("DESKPLAN  /  REFLECTION", 10, "Accent")); heading.Margin = new(0, 7, 0, 14); header.Children.Add(heading);
        var toolbar = new WrapPanel { Margin = new(0, 0, 0, 16) }; header.Children.Add(toolbar);
        if (!overdue)
        {
            period.SelectedIndex = 1; period.Margin = new(0, 0, 8, 0); date.Margin = new(0, 0, 8, 0);
            toolbar.Children.Add(period); toolbar.Children.Add(Ui.Button("‹ 上一期", () => Navigate(-1))); toolbar.Children.Add(date);
            toolbar.Children.Add(Ui.Button("下一期 ›", () => Navigate(1))); toolbar.Children.Add(Ui.Button("本期", () => date.SelectedDate = DateTime.Today));
        }
        toolbar.Children.Add(Ui.Button("导出 CSV", Export)); toolbar.Children.Add(Ui.Button("刷新", () => Render(false)));
        noteArea.Margin = new(0, 14, 0, 0); DockPanel.SetDock(noteArea, Dock.Bottom); root.Children.Add(noteArea);
        noteArea.Children.Add(Ui.Text("复盘笔记 · 写下成果、阻碍与下一步", 13)); note.Margin = new(0, 8, 0, 8); noteArea.Children.Add(note);
        var noteActions = new StackPanel { Orientation = Orientation.Horizontal }; noteArea.Children.Add(noteActions);
        noteActions.Children.Add(Ui.Button("保存总结", SaveNote, true)); noteActions.Children.Add(saved);
        root.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        period.SelectionChanged += (_, _) => { SaveNote(); Render(true); };
        date.SelectedDateChanged += (_, _) => { SaveNote(); Render(true); };
        note.TextChanged += (_, _) => { if (loadingNote) return; dirty = true; saved.Text = "正在保存…"; noteTimer.Stop(); noteTimer.Start(); };
        noteTimer.Tick += (_, _) => { noteTimer.Stop(); try { SaveNote(); } catch (Exception ex) { saved.Text = "保存失败：" + ex.Message; } };
        controller.Service.Changed += ServiceChanged;
        Closing += (_, e) => { try { SaveNote(); } catch (Exception ex) { e.Cancel = true; Ui.Error(this, ex); } };
        Closed += (_, _) => { noteTimer.Stop(); controller.Service.Changed -= ServiceChanged; };
        Render(true);
    }
    public void RefreshData() => Render(true);
    private void ServiceChanged() { if (!rendering) Render(false); }
    private (DateOnly Start, DateOnly End) Range => overdue ? (new(1900, 1, 1), DateOnly.FromDateTime(DateTime.Today).AddDays(-1))
        : CalendarRange.For((ReviewPeriod)period.SelectedIndex, DateOnly.FromDateTime(date.SelectedDate ?? DateTime.Today));
    private void Navigate(int direction)
    {
        var current = date.SelectedDate ?? DateTime.Today;
        date.SelectedDate = period.SelectedIndex switch { 1 => current.AddDays(direction * 7), 2 => current.AddMonths(direction), _ => current.AddDays(direction) };
    }
    private void Render(bool loadNote)
    {
        if (rendering) return;
        rendering = true;
        try
        {
            controller.Flush();
            var (start, end) = Range;
            var tasks = (overdue ? controller.Store.Occurrences(start, end).Where(t => !t.IsCompleted) : controller.Service.Get(start, end)).ToList();
            var stats = Statistics.For(tasks);
            heading.Text = overdue ? "仍值得被记住的计划" : $"{start:yyyy年M月d日} — {end:M月d日}";
            body.Children.Clear();
            var tiles = new System.Windows.Controls.Primitives.UniformGrid { Columns = 5, Margin = new(0, 0, 8, 8) };
            foreach (var pair in new[] { ("计划总数", stats.Total.ToString()), ("已完成", stats.Completed.ToString()), ("未完成", stats.Incomplete.ToString()),
                ("完成率", Format(stats.CompletionRate)), ("平均进度", Format(stats.AverageProgress)) })
            {
                var stack = new StackPanel(); stack.Children.Add(Ui.Text(pair.Item1, 11, "Muted"));
                var value = Ui.Text(pair.Item2, 25); value.Margin = new(0, 6, 0, 0); stack.Children.Add(value);
                var card = Ui.Card(stack); card.Margin = new(0, 0, 8, 0); tiles.Children.Add(card);
            }
            body.Children.Add(tiles);
            if (!overdue)
            {
                var daily = Enumerable.Range(0, end.DayNumber - start.DayNumber + 1).Select(i => start.AddDays(i))
                    .Select(day => (Date: day, Stats: Statistics.For(tasks.Where(t => t.ScheduledDate == day)))).ToList();
                if (period.SelectedIndex != 0)
                {
                    var chartBox = new StackPanel(); chartBox.Children.Add(Ui.Text("每日平均进度", 13));
                    chartBox.Children.Add(new TrendChart(daily.Select(d => (d.Date, d.Stats.AverageProgress)).ToList()) { Height = 135, Margin = new(0, 10, 0, 0) });
                    body.Children.Add(Ui.Card(chartBox));
                }
                if (period.SelectedIndex == 2)
                {
                    var calendar = new System.Windows.Controls.Primitives.UniformGrid { Columns = 7 };
                    foreach (var weekDay in new[] { "一", "二", "三", "四", "五", "六", "日" })
                    { var label = Ui.Text(weekDay, 11, "Muted"); label.HorizontalAlignment = HorizontalAlignment.Center; label.Margin = new(0, 0, 0, 7); calendar.Children.Add(label); }
                    for (var i = 0; i < ((int)start.DayOfWeek + 6) % 7; i++) calendar.Children.Add(new Border());
                    foreach (var day in daily)
                    {
                        var btn = Ui.Button($"{day.Date.Day}\n{Format(day.Stats.AverageProgress)}", () => { period.SelectedIndex = 0; date.SelectedDate = day.Date.ToDateTime(TimeOnly.MinValue); });
                        btn.Margin = new(2); btn.Padding = new(6); btn.ToolTip = $"{day.Date:yyyy-MM-dd} · {day.Stats.Total} 项计划，点击查看";
                        if (day.Stats.AverageProgress >= 80) btn.SetResourceReference(Button.BackgroundProperty, "AccentSoft");
                        calendar.Children.Add(btn);
                    }
                    body.Children.Add(Ui.Card(calendar));
                }
            }
            if (tasks.Count == 0) { var empty = Ui.Text(overdue ? "没有待处理的历史计划。" : "这段时间还没有计划；空日期不会计为已完成。", 14, "Muted"); empty.Margin = new(8, 25, 8, 25); body.Children.Add(empty); }
            foreach (var group in tasks.OrderBy(t => t.ScheduledDate).ThenBy(t => t.StartTime ?? TimeOnly.MaxValue).GroupBy(t => t.ScheduledDate))
            {
                var dayStats = Statistics.For(group);
                var list = new StackPanel();
                foreach (var task in group)
                {
                    var row = new DockPanel { Margin = new(0, 8, 0, 8) };
                    var edit = Ui.Button("编辑 / 改期", () => { controller.EditTask(task.Id); Render(false); }); DockPanel.SetDock(edit, Dock.Right); row.Children.Add(edit);
                    var info = new StackPanel(); var title = Ui.Text($"{task.StartTime?.ToString("HH:mm") ?? "未指定时间"}    {task.Title}");
                    if (task.IsCompleted) { title.TextDecorations = TextDecorations.Strikethrough; title.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); }
                    info.Children.Add(title); info.Children.Add(Ui.Text($"进度 {task.Progress}%" + (task.CompletedAt is { } done ? $"  ·  完成于 {done.LocalDateTime:MM-dd HH:mm}" : ""), 11, "Muted"));
                    row.Children.Add(info); list.Children.Add(row);
                }
                var expander = new Expander { Header = $"{group.Key:MM月dd日}  {group.Key.ToString("dddd", CultureInfo.GetCultureInfo("zh-CN"))}    {dayStats.Completed}/{dayStats.Total} 已完成 · 平均 {Format(dayStats.AverageProgress)}",
                    Content = list, IsExpanded = !overdue && period.SelectedIndex != 2, Padding = new(8) };
                expander.SetResourceReference(Control.ForegroundProperty, "Ink"); body.Children.Add(Ui.Card(expander, new(5)));
            }
            noteArea.Visibility = overdue || period.SelectedIndex == 0 ? Visibility.Collapsed : Visibility.Visible;
            if (loadNote)
            {
                displayedPeriod = (ReviewPeriod)period.SelectedIndex; displayedStart = start;
                loadingNote = true;
                note.Text = controller.Store.Reviews().SingleOrDefault(n => n.Period == displayedPeriod && n.PeriodStart == start)?.Content ?? "";
                loadingNote = false; dirty = false; saved.Text = "输入后自动保存到本机";
            }
        }
        finally { rendering = false; }
    }
    public void SaveNote()
    {
        if (!dirty || overdue) return;
        noteTimer.Stop();
        controller.Service.SaveReview(displayedPeriod, displayedStart, note.Text);
        dirty = false; saved.Text = "已保存 " + DateTime.Now.ToString("HH:mm");
    }
    private void Export()
    {
        try
        {
            controller.Flush(); var (start, end) = Range;
            var dialog = new SaveFileDialog { Filter = "CSV 表格 (*.csv)|*.csv", FileName = $"DeskPlan-{start:yyyyMMdd}-{end:yyyyMMdd}.csv" };
            if (dialog.ShowDialog(this) == true)
            {
                var tasks = overdue ? controller.Store.Occurrences(start, end).Where(t => !t.IsCompleted) : controller.Service.Get(start, end);
                BackupService.ExportCsv(tasks, dialog.FileName, start, end); MessageBox.Show(this, "CSV 已导出。", "DeskPlan");
            }
        }
        catch (Exception ex) { Ui.Error(this, ex); }
    }
    private static string Format(double? value) => value is { } number ? $"{number:0.#}%" : "--";
}

public sealed class TrendChart(IReadOnlyList<(DateOnly Date, double? Value)> points) : FrameworkElement
{
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var ink = (Brush)FindResource("Muted"); var accent = (Brush)FindResource("Accent"); var line = (Brush)FindResource("Line");
        var width = Math.Max(1, ActualWidth - 46); var height = Math.Max(1, ActualHeight - 30);
        FormattedText Text(string value) => new(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Microsoft YaHei UI"), 10, ink, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        foreach (var level in new[] { 0, 50, 100 })
        {
            var y = 4 + height * (1 - level / 100.0); dc.DrawLine(new(line, 1), new(30, y), new(30 + width, y)); dc.DrawText(Text(level.ToString()), new(0, y - 6));
        }
        Point? previous = null;
        for (var i = 0; i < points.Count; i++)
        {
            var x = 30 + (points.Count == 1 ? width / 2 : width * i / (points.Count - 1));
            if (points[i].Value is { } value)
            {
                var point = new Point(x, 4 + height * (1 - value / 100));
                if (previous is { } last) dc.DrawLine(new(accent, 2), last, point);
                dc.DrawEllipse(accent, null, point, 3, 3); previous = point;
            }
            else { previous = null; dc.DrawText(Text("--"), new(x - 5, height / 2)); }
            if (points.Count <= 7 || i % 5 == 0 || i == points.Count - 1)
                dc.DrawText(Text(points[i].Date.ToString("M/d")), new(x - 8, height + 12));
        }
    }
}
