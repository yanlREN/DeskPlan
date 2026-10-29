using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;
using DeskPlan.Core;
using DeskPlan.Infrastructure;

namespace DeskPlan.App.ViewModels;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
public sealed class TaskItemViewModel(TaskOccurrence model, Action<TaskItemViewModel> progressChanged) : Observable
{
    public TaskOccurrence Model { get; private set; } = model;
    public Guid Id => Model.Id;
    public string Title => Model.Title;
    public string TimeLabel => Model.StartTime is { } t ? t.ToString("HH:mm") + (Model.EndTime is { } end ? " – " + end.ToString("HH:mm") : "") : "未指定时间";
    public string Note => Model.Note;
    public int Progress
    {
        get => Model.Progress;
        set
        {
            if (Model.Progress == value) return;
            Model = Model.WithProgress(value, DateTimeOffset.UtcNow);
            Notify(); Notify(nameof(Percentage)); Notify(nameof(IsCompleted)); Notify(nameof(Status)); progressChanged(this);
        }
    }
    public string Percentage => $"{Progress}%";
    public bool IsCompleted => Progress == 100;
    public string Status => IsCompleted ? "已完成" : Progress == 0 ? "未开始" : "进行中";
    public void Saved(TaskOccurrence current) => Model = current;
}
public sealed class MainViewModel : Observable
{
    private readonly PlannerService service;
    private readonly Func<PlannerSettings> settings;
    private readonly Dictionary<Guid, TaskItemViewModel> pending = [];
    private readonly DispatcherTimer saveTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private DateTime selectedDate = DateTime.Today;
    private List<TaskItemViewModel> all = [];
    private string saveStatus = "本地保存 · 离线可用";
    private string modeName = "桌面模式";
    private string modeHint = "切换到悬浮模式";
    private bool isRevealed;
    public ObservableCollection<TaskItemViewModel> Tasks { get; } = [];
    public MainViewModel(PlannerService service, Func<PlannerSettings> settings)
    {
        this.service = service; this.settings = settings;
        saveTimer.Tick += (_, _) => { saveTimer.Stop(); try { Flush(); } catch (Exception ex) { SaveStatus = "保存失败：" + ex.Message; } };
        Refresh();
    }
    public DateTime SelectedDate
    {
        get => selectedDate;
        set
        {
            if (value.Date == selectedDate) return;
            Flush(); selectedDate = value.Date; Notify(); Refresh();
        }
    }
    public DateOnly Date => DateOnly.FromDateTime(selectedDate);
    public string DayTitle => selectedDate == DateTime.Today ? "今日计划" : selectedDate == DateTime.Today.AddDays(1) ? "明日计划" : "每日计划";
    public string DateLabel => selectedDate.ToString("M月d日  dddd", CultureInfo.GetCultureInfo("zh-CN"));
    public string Summary => all.Count == 0 ? "暂无计划" : $"已完成 {all.Count(t => t.IsCompleted)} / {all.Count}";
    public double Average => all.Count == 0 ? 0 : all.Average(t => t.Progress);
    public string AverageLabel => all.Count == 0 ? "平均进度  --" : $"平均进度 {Average:0.#}%";
    public string CompletionLabel => all.Count == 0 ? "完成率  --" : $"完成率 {100.0 * all.Count(t => t.IsCompleted) / all.Count:0.#}%";
    public Visibility EmptyVisibility => Tasks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public string EmptyTitle => all.Count > 0 ? "今天的计划已全部完成" : "留一点空间，给今天的计划";
    public string EmptySubtitle => all.Count > 0 ? "可在设置中展开已完成的计划" : "在下方写下第一件想做的事";
    public string SaveStatus { get => saveStatus; set { saveStatus = value; Notify(); } }
    public string ModeName { get => modeName; set { modeName = value; Notify(); } }
    public string ModeHint { get => modeHint; set { modeHint = value; Notify(); } }
    public bool IsRevealed { get => isRevealed; set { isRevealed = value; Notify(); Notify(nameof(ReturnDesktopVisibility)); } }
    public Visibility ReturnDesktopVisibility => IsRevealed ? Visibility.Visible : Visibility.Collapsed;
    public int OverdueCount => service.Store.Occurrences(new(1900, 1, 1), DateOnly.FromDateTime(DateTime.Today).AddDays(-1)).Count(t => !t.IsCompleted);
    public string OverdueLabel => $"待处理历史计划 · {OverdueCount}";
    public void Refresh()
    {
        Flush();
        var query = service.Get(Date, Date).AsEnumerable();
        query = settings().ManualSort ? query.OrderBy(t => t.DisplayOrder).ThenBy(t => t.CreatedAt)
            : query.OrderBy(t => t.StartTime ?? TimeOnly.MaxValue).ThenBy(t => t.DisplayOrder).ThenBy(t => t.CreatedAt);
        all = query.Select(t => new TaskItemViewModel(t, ProgressChanged)).ToList();
        Tasks.Clear(); foreach (var task in all.Where(t => !settings().CollapseCompleted || !t.IsCompleted)) Tasks.Add(task);
        UpdateLabels(); Notify(nameof(DayTitle)); Notify(nameof(DateLabel)); Notify(nameof(OverdueLabel));
    }
    private void ProgressChanged(TaskItemViewModel task)
    {
        pending[task.Id] = task; SaveStatus = "正在保存…"; UpdateLabels(); saveTimer.Stop(); saveTimer.Start();
    }
    private void UpdateLabels()
    { foreach (var name in new[] { nameof(Summary), nameof(Average), nameof(AverageLabel), nameof(CompletionLabel), nameof(EmptyVisibility), nameof(EmptyTitle), nameof(EmptySubtitle) }) Notify(name); }
    public void Flush()
    {
        saveTimer.Stop();
        foreach (var item in pending.ToArray())
        {
            item.Value.Saved(service.Progress(item.Key, item.Value.Progress)); pending.Remove(item.Key);
        }
        SaveStatus = "已保存到本机";
    }
    public void Move(Guid dragged, Guid target)
    {
        Flush();
        var ids = all.Select(t => t.Id).ToList();
        if (dragged == target || !ids.Contains(dragged) || !ids.Contains(target)) return;
        ids.Remove(dragged); ids.Insert(ids.IndexOf(target), dragged); service.Reorder(Date, ids);
    }
}
