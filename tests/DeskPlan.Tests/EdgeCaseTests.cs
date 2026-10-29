using DeskPlan.Core;
using DeskPlan.Infrastructure;
using Xunit;

namespace DeskPlan.Tests;

public sealed class EdgeCaseTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "DeskPlanTests", Guid.NewGuid().ToString("N"));
    private readonly PlannerStore store;
    private readonly PlannerService service;
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.Today);
    public EdgeCaseTests() { store = new(Path.Combine(folder, "test.db")); service = new(store); }
    [Fact] public void ConvertingMovedSingleToRecurringDoesNotDuplicateStartDay()
    {
        var t = service.Add(new("单次", Today));
        service.Edit(t.Id, new("每天", Today.AddDays(1), Repeat: RepeatRule.Daily), false);
        Assert.Single(service.Get(Today.AddDays(1), Today.AddDays(1)));
        Assert.Empty(service.Get(Today, Today));
        Assert.Equal(3, service.Get(Today, Today.AddDays(3)).Count);
    }
    [Fact] public void SeriesEditPreservesIndividuallyCustomizedFutureTask()
    {
        service.Add(new("原模板", Today, Repeat: RepeatRule.Daily));
        var tasks = service.Get(Today, Today.AddDays(2)).OrderBy(t => t.ScheduledDate).ToArray();
        service.Edit(tasks[2].Id, new("特别安排", Today.AddDays(2)), false);
        service.Edit(tasks[1].Id, new("新模板", Today.AddDays(1), Repeat: RepeatRule.Daily), true);
        Assert.Equal("特别安排", store.Find(tasks[2].Id)!.Title);
    }
    [Fact] public void SeriesEditCannotOverwriteAnInstanceMovedIntoHistory()
    {
        service.Add(new("重复", Today, Repeat: RepeatRule.Daily));
        var tasks = service.Get(Today, Today.AddDays(2)).OrderBy(t => t.ScheduledDate).ToArray();
        service.Edit(tasks[2].Id, new("历史记录", Today.AddDays(-1)), false);
        service.Edit(tasks[0].Id, new("以后", Today, Repeat: RepeatRule.Daily), true);
        Assert.Equal("历史记录", store.Find(tasks[2].Id)!.Title);
    }
    [Fact] public void DeletionDoesNotChangeOtherDailyOccurrences()
    {
        var one = service.Add(new("每天", Today, Repeat: RepeatRule.Daily));
        service.Delete(one.Id); Assert.Empty(service.Get(Today, Today));
        Assert.Single(service.Get(Today.AddDays(1), Today.AddDays(1)));
    }
    [Fact] public void WeekdayPlanOnSaturdayStartsMonday()
    {
        var sat = new DateOnly(2026, 10, 31);
        var task = service.Add(new("工作日", sat, Repeat: RepeatRule.Weekdays));
        Assert.Equal(new DateOnly(2026, 11, 2), task.ScheduledDate);
        Assert.Empty(service.Get(sat, sat.AddDays(1)));
    }
    [Fact] public void DuplicateCompletionDoesNotResetCompletedTime()
    {
        var t = service.Add(new("完成", Today)); var first = service.Progress(t.Id, 100);
        Assert.Equal(first.CompletedAt, service.Progress(t.Id, 100).CompletedAt);
        service.Now = () => DateTimeOffset.UtcNow.AddHours(1); service.Progress(t.Id, 99);
        Assert.True(service.Progress(t.Id, 100).CompletedAt > first.CompletedAt);
    }
    [Fact] public void MissingBackupFieldsAreRejected()
    {
        var path = Path.Combine(folder, "missing.json"); File.WriteAllText(path, "{}");
        Assert.Throws<InvalidDataException>(() => BackupService.Read(path));
    }
    [Fact] public void BackupRejectsDuplicateRecurrenceIdentityAndOrphans()
    {
        var t = service.Add(new("保留", Today)); var d = store.Snapshot();
        Assert.Throws<InvalidDataException>(() => BackupService.Validate(d with { Occurrences = [t, t with { Id = Guid.NewGuid() }] }));
        Assert.Throws<InvalidDataException>(() => BackupService.Validate(d with { Occurrences = [t with { DefinitionId = Guid.NewGuid() }] }));
    }
    [Fact] public void ReorderingPersistsWithoutChangingProgress()
    {
        var a = service.Add(new("A", Today)); var b = service.Add(new("B", Today)); service.Progress(a.Id, 80);
        service.Reorder(Today, [b.Id, a.Id]);
        Assert.Equal(0, store.Find(b.Id)!.DisplayOrder); Assert.Equal(1, store.Find(a.Id)!.DisplayOrder); Assert.Equal(80, store.Find(a.Id)!.Progress);
    }
    [Fact] public void AdvanceReminderCanOccurOnPreviousCalendarDay()
    {
        var task = service.Add(new("午夜", new(2026, 10, 30), new TimeOnly(0, 15), ReminderEnabled: true, AdvanceMinutes: 30));
        var now = new DateTimeOffset(2026, 10, 29, 23, 45, 0, TimeSpan.FromHours(8));
        var zone = TimeZoneInfo.FindSystemTimeZoneById("China Standard Time");
        Assert.NotNull(ReminderRules.Due(task, now, zone));
    }
    [Fact] public void FailedRestoreValidationRetainsSettingsAndReviews()
    {
        store.Save(new PlannerSettings { Theme = AppTheme.Dark }); service.SaveReview(ReviewPeriod.Month, Today, "保留总结");
        var d = store.Snapshot() with { Settings = new PlannerSettings { Width = double.NaN } };
        Assert.Throws<InvalidDataException>(() => store.Restore(d));
        Assert.Equal(AppTheme.Dark, store.Settings().Theme); Assert.Equal("保留总结", store.Reviews().Single().Content);
    }
    [Theory] [InlineData("", false)] [InlineData("计划", true)]
    public void DraftValidationRejectsMissingTitleAndMissingReminderTime(string title, bool reminder)
    { Assert.Throws<ArgumentException>(() => service.Add(new(title, Today, ReminderEnabled: reminder))); }
    public void Dispose() { store.Dispose(); if (Directory.Exists(folder)) Directory.Delete(folder, true); }
}
