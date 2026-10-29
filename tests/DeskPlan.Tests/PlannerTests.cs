using DeskPlan.Core;
using DeskPlan.Infrastructure;
using Xunit;

namespace DeskPlan.Tests;

public sealed class PlannerTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "DeskPlanTests", Guid.NewGuid().ToString("N"));
    private readonly PlannerStore store;
    private readonly PlannerService service;
    private static readonly DateOnly Monday = new(2026, 10, 26);
    public PlannerTests()
    {
        store = new(Path.Combine(folder, "test.db"));
        service = new(store) { Now = () => new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.FromHours(8)) };
    }
    [Fact] public void ProgressCompletesReopensAndRetainsEvents()
    {
        var task = service.Add(new("写作", Monday));
        var done = service.Progress(task.Id, 100);
        Assert.True(done.IsCompleted); Assert.NotNull(done.CompletedAt);
        var reopened = service.Progress(task.Id, 80);
        Assert.False(reopened.IsCompleted); Assert.Null(reopened.CompletedAt);
        Assert.Equal(2, store.ProgressEvents().Count);
        service.Progress(task.Id, 80);
        Assert.Equal(2, store.ProgressEvents().Count);
        Assert.Throws<ArgumentOutOfRangeException>(() => service.Progress(task.Id, 101));
    }
    [Fact] public void PersistedProgressSurvivesAnotherConnection()
    {
        var task = service.Add(new("保存", Monday)); service.Progress(task.Id, 100);
        using var second = new PlannerStore(store.DatabasePath);
        Assert.Equal(100, second.Find(task.Id)!.Progress); Assert.NotNull(second.Find(task.Id)!.CompletedAt);
    }
    [Fact] public void EmptyStatisticsAreUnknown()
    { var s = Statistics.For([]); Assert.Null(s.AverageProgress); Assert.Null(s.CompletionRate); }
    [Fact] public void CompletionRateAndAverageDiffer()
    {
        var one = service.Add(new("A", Monday)); var two = service.Add(new("B", Monday));
        service.Progress(one.Id, 100); service.Progress(two.Id, 50);
        var s = Statistics.For(service.Get(Monday, Monday)); Assert.Equal(50, s.CompletionRate); Assert.Equal(75, s.AverageProgress);
    }
    [Fact] public void DailyOccurrencesHaveIndependentProgressAndAreIdempotent()
    {
        var one = service.Add(new("每日", Monday, Repeat: RepeatRule.Daily)); service.Progress(one.Id, 100);
        var next = service.Get(Monday.AddDays(1), Monday.AddDays(1)).Single(); Assert.Equal(0, next.Progress);
        service.Get(Monday, Monday.AddDays(1)); Assert.Equal(2, store.AllOccurrences().Count);
    }
    [Fact] public void DeletedAndMovedInstancesNeverRegenerate()
    {
        var task = service.Add(new("重复", Monday, Repeat: RepeatRule.Daily));
        service.Edit(task.Id, new("移期", Monday.AddDays(3)), false);
        Assert.Empty(service.Get(Monday, Monday));
        service.Delete(task.Id); service.Materialize(Monday, Monday.AddDays(4));
        Assert.True(store.Find(task.Id)!.Deleted);
        Assert.Single(store.AllOccurrences(), t => t.OriginalDate == Monday);
    }
    [Theory] [InlineData(RepeatRule.Weekdays, 5)] [InlineData(RepeatRule.Weekly, 1)]
    public void RecurrenceRespectsWeek(RepeatRule rule, int count)
    { service.Add(new("周期", Monday, Repeat: rule)); Assert.Equal(count, service.Get(Monday, Monday.AddDays(6)).Count); }
    [Fact] public void FutureSeriesEditsPreservePastSnapshotsAndCurrentProgress()
    {
        service.Add(new("旧标题", Monday, Repeat: RepeatRule.Daily));
        var tasks = service.Get(Monday, Monday.AddDays(3)).OrderBy(t => t.ScheduledDate).ToList();
        service.Progress(tasks[1].Id, 65);
        service.Edit(tasks[1].Id, new("新标题", Monday.AddDays(1), Repeat: RepeatRule.Daily), true);
        Assert.Equal("旧标题", store.Find(tasks[0].Id)!.Title);
        Assert.Equal("新标题", store.Find(tasks[1].Id)!.Title); Assert.Equal(65, store.Find(tasks[1].Id)!.Progress);
        Assert.Equal(4, service.Get(Monday, Monday.AddDays(3)).Count);
    }
    [Fact] public void HistoricalEditsAreAudited()
    {
        var task = service.Add(new("旧", Monday.AddDays(-2)));
        service.Edit(task.Id, new("修正", task.ScheduledDate), false);
        Assert.Contains(store.AuditEvents(), a => a.Action == "编辑本次" && a.Before.Contains("旧"));
        Assert.Throws<ArgumentException>(() => service.Edit(task.Id, new("不覆盖", task.ScheduledDate), true));
    }
    [Fact] public void WeekStartsMondayAndMonthHandlesLeapYear()
    {
        Assert.Equal(Monday, CalendarRange.WeekStart(Monday.AddDays(6)));
        Assert.Equal(new DateOnly(2024, 2, 29), CalendarRange.For(ReviewPeriod.Month, new(2024, 2, 2)).End);
    }
    [Fact] public void StatisticsGroupByScheduledDate()
    {
        service.Add(new("上月", new(2026, 9, 30))); service.Add(new("本月", new(2026, 10, 1)));
        Assert.Single(service.Get(new(2026, 9, 1), new(2026, 9, 30)));
    }
    [Fact] public void ReviewsPersistAndUpdateWithoutDuplication()
    {
        service.SaveReview(ReviewPeriod.Week, Monday.AddDays(2), "总结一"); service.SaveReview(ReviewPeriod.Week, Monday, "总结二");
        using var second = new PlannerStore(store.DatabasePath);
        Assert.Equal("总结二", second.Reviews().Single().Content);
    }
    [Fact] public void BackupRoundTripRetainsEverythingAndCreatesSafetyCopy()
    {
        var task = service.Add(new("备份", Monday)); service.Progress(task.Id, 75);
        service.SaveReview(ReviewPeriod.Month, Monday, "月总结");
        var path = Path.Combine(folder, "backup.json"); BackupService.Export(store, path);
        service.Delete(task.Id);
        var safety = store.Restore(BackupService.Read(path));
        Assert.True(File.Exists(safety)); Assert.False(store.Find(task.Id)!.Deleted);
        Assert.Equal(75, store.Find(task.Id)!.Progress); Assert.Single(store.Reviews()); Assert.Single(store.ProgressEvents());
    }
    [Fact] public void InvalidBackupCannotOverwriteDatabase()
    {
        var task = service.Add(new("保留", Monday));
        var invalid = store.Snapshot() with { Version = 999 };
        Assert.Throws<InvalidDataException>(() => store.Restore(invalid)); Assert.NotNull(store.Find(task.Id));
        invalid = store.Snapshot() with { Occurrences = [task with { Progress = 100, CompletedAt = null }] };
        Assert.Throws<InvalidDataException>(() => store.Restore(invalid));
    }
    [Fact] public void TransactionRollsBackOnFailure()
    {
        var def = TaskDefinition.From(new("回滚", Monday));
        Assert.Throws<InvalidOperationException>(() => store.Transaction(() => { store.Save(def); throw new InvalidOperationException(); }));
        Assert.Empty(store.Definitions());
    }
    [Fact] public void RemindersSkipCompletedOldAndAlreadyDelivered()
    {
        var now = new DateTimeOffset(2026, 10, 26, 14, 0, 0, TimeSpan.Zero);
        var task = service.Add(new("提醒", Monday, new TimeOnly(14, 0), ReminderEnabled: true));
        var due = ReminderRules.Due(task, now, TimeZoneInfo.Utc); Assert.NotNull(due);
        Assert.Null(ReminderRules.Due(task with { LastReminderKey = due.Value.Key }, now, TimeZoneInfo.Utc));
        Assert.Null(ReminderRules.Due(task.WithProgress(100, now), now, TimeZoneInfo.Utc));
        Assert.Null(ReminderRules.Due(task, now.AddHours(3), TimeZoneInfo.Utc));
        Assert.Null(ReminderRules.Due(task, now.AddMinutes(-1), TimeZoneInfo.Utc));
    }
    [Fact] public void SnoozePersistsAndIsDeliveredOnce()
    {
        var task = service.Add(new("稍后", Monday, new TimeOnly(12, 0), ReminderEnabled: true));
        service.Snooze(task.Id); var saved = store.Find(task.Id)!;
        Assert.Null(ReminderRules.Due(saved, service.Now(), TimeZoneInfo.Local));
        var due = ReminderRules.Due(saved, service.Now().AddMinutes(10), TimeZoneInfo.Local);
        Assert.NotNull(due); service.MarkReminderDelivered(task.Id, due.Value.Key);
        Assert.Null(ReminderRules.Due(store.Find(task.Id)!, service.Now().AddMinutes(10), TimeZoneInfo.Local));
    }
    [Fact] public void CsvEscapesMultilineTextAndNeutralizesFormulas()
    {
        service.Add(new("=1+1", Monday, Note: "备注,\"引号\"\n下一行"));
        var path = Path.Combine(folder, "test.csv"); BackupService.ExportCsv(service.Get(Monday, Monday), path, Monday, Monday);
        var csv = File.ReadAllText(path); Assert.Contains("'=1+1", csv); Assert.Contains("\"\"引号\"\"", csv); Assert.Contains("2026-10-26", csv);
    }
    [Fact] public void CatchUpPreservesMissedDays()
    {
        service.Add(new("跨天", Monday, Repeat: RepeatRule.Daily)); service.CatchUp(Monday.AddDays(7));
        Assert.Equal(8, store.AllOccurrences().Count); Assert.All(store.AllOccurrences(), t => Assert.Equal(0, t.Progress));
    }
    public void Dispose() { store.Dispose(); if (Directory.Exists(folder)) Directory.Delete(folder, true); }
}
