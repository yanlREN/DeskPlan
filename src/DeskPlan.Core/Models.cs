namespace DeskPlan.Core;

public enum RepeatRule { None, Daily, Weekdays, Weekly }
public enum DisplayMode { Desktop, Floating }
public enum AppTheme { Light, Dark, Glass }
public enum ReviewPeriod { Day, Week, Month }

public sealed record TaskDraft(string Title, DateOnly Date, TimeOnly? StartTime = null,
    TimeOnly? EndTime = null, string Note = "", RepeatRule Repeat = RepeatRule.None,
    bool ReminderEnabled = false, int AdvanceMinutes = 0)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Title) || Title.Trim().Length > 200)
            throw new ArgumentException("标题不能为空，且不能超过 200 个字符。");
        if (Note.Length > 20000) throw new ArgumentException("备注不能超过 20000 个字符。");
        if (EndTime.HasValue && (!StartTime.HasValue || EndTime <= StartTime))
            throw new ArgumentException("结束时间必须晚于开始时间（跨天计划请拆成两条）。");
        if (ReminderEnabled && StartTime is null) throw new ArgumentException("请先设置开始时间再开启提醒。");
        if (AdvanceMinutes is < 0 or > 1440) throw new ArgumentException("提前提醒范围为 0–1440 分钟。");
        if (!Enum.IsDefined(Repeat)) throw new ArgumentException("重复规则无效。");
        if (Date.Year is < 1900 or > 9998) throw new ArgumentException("计划日期范围为 1900–9998 年。");
    }
}

public sealed record TaskDefinition
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Title { get; init; } = "";
    public string Note { get; init; } = "";
    public DateOnly StartDate { get; init; }
    public DateOnly? Until { get; init; }
    public TimeOnly? StartTime { get; init; }
    public TimeOnly? EndTime { get; init; }
    public RepeatRule Repeat { get; init; }
    public bool ReminderEnabled { get; init; }
    public int AdvanceMinutes { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool Includes(DateOnly date) => date >= StartDate && (Until is null || date <= Until) && (Repeat switch
    {
        RepeatRule.None => date == StartDate,
        RepeatRule.Daily => true,
        RepeatRule.Weekdays => date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday),
        RepeatRule.Weekly => date.DayOfWeek == StartDate.DayOfWeek,
        _ => false
    });

    public static TaskDefinition From(TaskDraft draft) => new()
    {
        Title = draft.Title.Trim(), Note = draft.Note, StartDate = draft.Date,
        StartTime = draft.StartTime, EndTime = draft.EndTime, Repeat = draft.Repeat,
        ReminderEnabled = draft.ReminderEnabled, AdvanceMinutes = draft.AdvanceMinutes
    };

    public TaskOccurrence CreateOccurrence(DateOnly date, int order = 0) => new()
    {
        DefinitionId = Id, OriginalDate = date, ScheduledDate = date, Title = Title, Note = Note,
        StartTime = StartTime, EndTime = EndTime, ReminderEnabled = ReminderEnabled,
        AdvanceMinutes = AdvanceMinutes, DisplayOrder = order
    };
}

public sealed record TaskOccurrence
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid DefinitionId { get; init; }
    // OriginalDate is the recurrence identity; moving an instance must not recreate it.
    public DateOnly OriginalDate { get; init; }
    public DateOnly ScheduledDate { get; init; }
    public string Title { get; init; } = "";
    public string Note { get; init; } = "";
    public TimeOnly? StartTime { get; init; }
    public TimeOnly? EndTime { get; init; }
    public int Progress { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public int DisplayOrder { get; init; }
    public bool Deleted { get; init; }
    public bool IsOverride { get; init; }
    public bool ReminderEnabled { get; init; }
    public int AdvanceMinutes { get; init; }
    public DateTimeOffset? SnoozedUntil { get; init; }
    public string? LastReminderKey { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool IsCompleted => Progress == 100;

    public TaskOccurrence WithProgress(int value, DateTimeOffset now)
    {
        if (value is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(value));
        return this with { Progress = value, CompletedAt = value == 100 ? CompletedAt ?? now : null, UpdatedAt = now };
    }
}

public sealed record ProgressEvent(Guid Id, Guid OccurrenceId, int OldProgress, int NewProgress, DateTimeOffset ChangedAt);
public sealed record AuditEvent(Guid Id, Guid? OccurrenceId, string Action, string Before, string After, DateTimeOffset ChangedAt);
public sealed record ReviewNote(Guid Id, ReviewPeriod Period, DateOnly PeriodStart, string Content, DateTimeOffset UpdatedAt);

public sealed record PlannerSettings
{
    public DisplayMode Mode { get; init; } = DisplayMode.Desktop;
    public AppTheme Theme { get; init; } = AppTheme.Light;
    public double BackgroundOpacity { get; init; } = 0.96;
    public double FontSize { get; init; } = 13;
    public double Left { get; init; } = 60;
    public double Top { get; init; } = 80;
    public double Width { get; init; } = 360;
    public double Height { get; init; } = 510;
    public bool Autostart { get; init; }
    public bool CloseToTray { get; init; } = true;
    public bool ManualSort { get; init; }
    public bool CollapseCompleted { get; init; }
}

public sealed record BackupDocument
{
    public int Version { get; init; } = 1;
    public DateTimeOffset ExportedAt { get; init; } = DateTimeOffset.UtcNow;
    public List<TaskDefinition> Definitions { get; init; } = [];
    public List<TaskOccurrence> Occurrences { get; init; } = [];
    public List<ProgressEvent> ProgressEvents { get; init; } = [];
    public List<AuditEvent> AuditEvents { get; init; } = [];
    public List<ReviewNote> Reviews { get; init; } = [];
    public PlannerSettings Settings { get; init; } = new();
}
