using DeskPlan.Core;

namespace DeskPlan.Infrastructure;

public sealed class PlannerService(PlannerStore store)
{
    public PlannerStore Store { get; } = store;
    public event Action? Changed;
    public Func<DateTimeOffset> Now { get; set; } = () => DateTimeOffset.UtcNow;

    public void Materialize(DateOnly start, DateOnly end)
    {
        if (end < start) throw new ArgumentException("日期范围无效。");
        var definitions = Store.Definitions();
        var existing = Store.AllOccurrences().Select(o => (o.DefinitionId, o.OriginalDate)).ToHashSet();
        var additions = new List<TaskOccurrence>();
        foreach (var definition in definitions)
        {
            var first = start > definition.StartDate ? start : definition.StartDate;
            var last = definition.Until is { } until && until < end ? until : end;
            if (definition.Repeat == RepeatRule.None && last > definition.StartDate) last = definition.StartDate;
            for (var date = first; date <= last; date = date.AddDays(1))
                if (definition.Includes(date) && existing.Add((definition.Id, date)))
                    additions.Add(definition.CreateOccurrence(date, additions.Count));
        }
        if (additions.Count > 0) Store.Transaction(() => { foreach (var item in additions) Store.Save(item); });
    }
    public void CatchUp(DateOnly today)
    {
        var definitions = Store.Definitions();
        if (definitions.Count > 0) Materialize(definitions.Min(d => d.StartDate), today);
    }
    public List<TaskOccurrence> Get(DateOnly start, DateOnly end)
    {
        Materialize(start, end);
        return Store.Occurrences(start, end);
    }
    public TaskOccurrence Add(TaskDraft draft)
    {
        draft.Validate();
        var definition = TaskDefinition.From(draft);
        // A weekday rule created on a weekend starts on the next weekday.
        var date = draft.Date;
        while (!definition.Includes(date)) date = date.AddDays(1);
        var task = definition.CreateOccurrence(date, Store.Occurrences(date, date).Select(t => t.DisplayOrder).DefaultIfEmpty(-1).Max() + 1);
        Store.Transaction(() => { Store.Save(definition); Store.Save(task); Audit(task.Id, "添加", null, task); });
        Changed?.Invoke();
        return task;
    }
    public TaskOccurrence Progress(Guid id, int value)
    {
        var old = Required(id);
        if (old.Progress == value) return old;
        var current = old.WithProgress(value, Now());
        Store.Transaction(() =>
        {
            Store.Save(current);
            Store.Save(new ProgressEvent(Guid.NewGuid(), id, old.Progress, value, Now()));
        });
        Changed?.Invoke();
        return current;
    }
    public void Edit(Guid id, TaskDraft draft, bool future)
    {
        draft.Validate();
        var old = Required(id);
        var definition = Store.Definitions().Single(d => d.Id == old.DefinitionId);
        if (future && old.ScheduledDate < DateOnly.FromDateTime(Now().LocalDateTime))
            throw new ArgumentException("历史实例请使用“仅本次”，以保护已经发生的记录。");
        if (future && draft.Date != old.ScheduledDate)
            throw new ArgumentException("修改“本次及以后”时请保持本次日期；单次改期请用“仅本次”。");
        if (future && old.OriginalDate != old.ScheduledDate)
            throw new ArgumentException("这次计划已经单独改期，请选择“仅本次”；调整重复规则请从尚未改期的实例进入。");
        Store.Transaction(() =>
        {
            if (!future)
            {
                // Turning a single plan into a series also creates future independent occurrences.
                if (definition.Repeat == RepeatRule.None && draft.Repeat != RepeatRule.None)
                {
                    var changed = TaskDefinition.From(draft) with { Id = definition.Id, CreatedAt = definition.CreatedAt };
                    Store.Save(changed);
                }
                var current = Apply(old, draft) with { IsOverride = true };
                if (definition.Repeat == RepeatRule.None && draft.Repeat != RepeatRule.None)
                    current = current with { OriginalDate = draft.Date };
                Store.Save(current);
                Audit(id, "编辑本次", old, current);
            }
            else
            {
                var boundary = old.OriginalDate;
                Store.Save(definition with { Until = boundary.AddDays(-1), UpdatedAt = Now() });
                var next = TaskDefinition.From(draft);
                Store.Save(next);
                // Preserve original IDs, completion and progress wherever the new recurrence includes the date.
                var targets = Store.AllOccurrences().Where(o => o.DefinitionId == definition.Id && o.OriginalDate >= boundary).ToList();
                foreach (var target in targets)
                {
                    // A future occurrence can have been individually moved into the past.
                    // Its existing history is immutable under a future-series edit.
                    if (target.ScheduledDate < DateOnly.FromDateTime(Now().LocalDateTime)) continue;
                    TaskOccurrence current;
                    if (next.Includes(target.OriginalDate))
                        current = target.IsOverride && target.Id != id
                            ? target with { DefinitionId = next.Id, UpdatedAt = Now() }
                            : Apply(target, draft with { Date = target.ScheduledDate }) with { DefinitionId = next.Id, IsOverride = false };
                    else current = target with { Deleted = true, UpdatedAt = Now() };
                    Store.Save(current);
                    Audit(target.Id, "编辑本次及以后", target, current);
                }
            }
        });
        Changed?.Invoke();
    }
    private TaskOccurrence Apply(TaskOccurrence old, TaskDraft draft) => old with
    {
        Title = draft.Title.Trim(), Note = draft.Note, ScheduledDate = draft.Date,
        StartTime = draft.StartTime, EndTime = draft.EndTime, ReminderEnabled = draft.ReminderEnabled,
        AdvanceMinutes = draft.AdvanceMinutes, UpdatedAt = Now(), SnoozedUntil = null,
        LastReminderKey = old.ScheduledDate == draft.Date && old.StartTime == draft.StartTime && old.AdvanceMinutes == draft.AdvanceMinutes
            ? old.LastReminderKey : null
    };
    public void Delete(Guid id)
    {
        var old = Required(id);
        var current = old with { Deleted = true, UpdatedAt = Now(), IsOverride = true };
        Store.Transaction(() => { Store.Save(current); Audit(id, "删除本次", old, current); });
        Changed?.Invoke();
    }
    public void Reorder(DateOnly date, IReadOnlyList<Guid> ids)
    {
        var tasks = Store.Occurrences(date, date).ToDictionary(t => t.Id);
        if (ids.Distinct().Count() != ids.Count || ids.Any(id => !tasks.ContainsKey(id))) throw new ArgumentException("排序列表无效。");
        Store.Transaction(() =>
        {
            for (var i = 0; i < ids.Count; i++) Store.Save(tasks[ids[i]] with { DisplayOrder = i, UpdatedAt = Now() });
            Audit(null, "调整顺序", null, ids);
        });
        Changed?.Invoke();
    }
    public void SaveReview(ReviewPeriod period, DateOnly date, string text)
    {
        var start = CalendarRange.For(period, date).Start;
        var old = Store.Reviews().SingleOrDefault(r => r.Period == period && r.PeriodStart == start);
        var current = new ReviewNote(old?.Id ?? Guid.NewGuid(), period, start, text, Now());
        Store.Transaction(() => { Store.Save(current); Audit(null, "保存总结", old, current); });
        Changed?.Invoke();
    }
    public void MarkReminderDelivered(Guid id, string key)
    {
        var task = Required(id);
        Store.Transaction(() => Store.Save(task with { LastReminderKey = key }));
    }
    public void Snooze(Guid id)
    {
        var task = Required(id);
        if (task.IsCompleted) return;
        Store.Transaction(() => Store.Save(task with { SnoozedUntil = Now().AddMinutes(10), LastReminderKey = null }));
    }
    private TaskOccurrence Required(Guid id) => Store.Find(id) is { Deleted: false } task ? task : throw new ArgumentException("这条计划已不存在。");
    private void Audit(Guid? id, string action, object? before, object? after) =>
        Store.Save(new AuditEvent(Guid.NewGuid(), id, action, before is null ? "" : PlannerStore.Encode(before),
            after is null ? "" : PlannerStore.Encode(after), Now()));
}
