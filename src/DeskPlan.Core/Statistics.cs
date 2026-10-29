namespace DeskPlan.Core;

public sealed record Statistics(int Total, int Completed, double? CompletionRate, double? AverageProgress)
{
    public int Incomplete => Total - Completed;
    public static Statistics For(IEnumerable<TaskOccurrence> source)
    {
        var tasks = source.Where(t => !t.Deleted).ToArray();
        var completed = tasks.Count(t => t.Progress == 100);
        return new(tasks.Length, completed, tasks.Length == 0 ? null : 100.0 * completed / tasks.Length,
            tasks.Length == 0 ? null : tasks.Average(t => t.Progress));
    }
}

public static class CalendarRange
{
    public static DateOnly WeekStart(DateOnly date) => date.AddDays(-(((int)date.DayOfWeek + 6) % 7));
    public static (DateOnly Start, DateOnly End) For(ReviewPeriod period, DateOnly date) => period switch
    {
        ReviewPeriod.Week => (WeekStart(date), WeekStart(date).AddDays(6)),
        ReviewPeriod.Month => (new(date.Year, date.Month, 1), new(date.Year, date.Month, DateTime.DaysInMonth(date.Year, date.Month))),
        _ => (date, date)
    };
}

public static class ReminderRules
{
    public static (DateTimeOffset Due, string Key)? Due(TaskOccurrence task, DateTimeOffset now, TimeZoneInfo zone)
    {
        if (task.Deleted || task.IsCompleted || !task.ReminderEnabled || task.StartTime is null) return null;
        DateTimeOffset due;
        string key;
        if (task.SnoozedUntil is { } snoozed)
        {
            due = snoozed;
            key = "snooze:" + snoozed.ToString("O");
        }
        else
        {
            var local = task.ScheduledDate.ToDateTime(task.StartTime.Value, DateTimeKind.Unspecified);
            if (zone.IsInvalidTime(local)) local = local.AddHours(1);
            due = new DateTimeOffset(local, zone.GetUtcOffset(local)).AddMinutes(-task.AdvanceMinutes);
            key = due.ToUniversalTime().ToString("O");
        }
        // A two-hour catch-up window handles sleep/restart without replaying old reminders.
        return due <= now && now - due <= TimeSpan.FromHours(2) && task.LastReminderKey != key ? (due, key) : null;
    }
}
