using System.Globalization;
using System.Text;
using System.Text.Json;
using DeskPlan.Core;

namespace DeskPlan.Infrastructure;

public static class BackupService
{
    public static void Export(PlannerStore store, string path) => AtomicWrite(path, PlannerStore.Encode(store.Snapshot()));
    public static BackupDocument Read(string path)
    {
        if (new FileInfo(path).Length > 100 * 1024 * 1024) throw new InvalidDataException("备份超过 100 MB，请拆分或联系维护者检查。");
        var text = File.ReadAllText(path);
        using var parsed = JsonDocument.Parse(text);
        if (parsed.RootElement.ValueKind != JsonValueKind.Object ||
            new[] { "Version", "ExportedAt", "Definitions", "Occurrences", "ProgressEvents", "AuditEvents", "Reviews", "Settings" }
                .Any(name => !parsed.RootElement.TryGetProperty(name, out _)))
            throw new InvalidDataException("备份缺少版本号或必要字段，请选择 DeskPlan 导出的完整 JSON 文件。");
        var document = JsonSerializer.Deserialize<BackupDocument>(text, PlannerStore.Json)
            ?? throw new InvalidDataException("备份文件为空。");
        Validate(document);
        return document;
    }
    public static void Validate(BackupDocument d)
    {
        static void Require([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool value, string message)
        { if (!value) throw new InvalidDataException("备份校验失败：" + message); }
        Require(d.Version == 1, "不支持的结构版本。");
        if (d.Definitions is null || d.Occurrences is null || d.ProgressEvents is null || d.AuditEvents is null || d.Reviews is null || d.Settings is null)
            throw new InvalidDataException("备份缺少必要数据集合。");
        static bool Unique(IEnumerable<Guid> ids) { var list = ids.ToList(); return list.All(id => id != Guid.Empty) && list.Distinct().Count() == list.Count; }
        Require(d.Definitions.All(t => t is not null) && d.Occurrences.All(t => t is not null) && d.ProgressEvents.All(t => t is not null)
            && d.AuditEvents.All(t => t is not null) && d.Reviews.All(t => t is not null), "存在空记录。");
        Require(Unique(d.Definitions.Select(t => t.Id)) && Unique(d.Occurrences.Select(t => t.Id)) && Unique(d.ProgressEvents.Select(t => t.Id))
            && Unique(d.AuditEvents.Select(t => t.Id)) && Unique(d.Reviews.Select(t => t.Id)), "重复或空 ID。");
        foreach (var def in d.Definitions)
        {
            Require(def.Title is not null && def.Note is not null, "模板文字为空值。");
            new TaskDraft(def.Title, def.StartDate, def.StartTime, def.EndTime, def.Note, def.Repeat, def.ReminderEnabled, def.AdvanceMinutes).Validate();
        }
        var definitions = d.Definitions.Select(t => t.Id).ToHashSet();
        var occurrences = d.Occurrences.Select(t => t.Id).ToHashSet();
        Require(d.Occurrences.Select(o => (o.DefinitionId, o.OriginalDate)).Distinct().Count() == d.Occurrences.Count, "重复的计划实例。");
        foreach (var task in d.Occurrences)
        {
            Require(definitions.Contains(task.DefinitionId), "实例没有对应模板。");
            Require(task.Title is not null && task.Note is not null, "任务文字为空值。");
            new TaskDraft(task.Title, task.ScheduledDate, task.StartTime, task.EndTime, task.Note,
                ReminderEnabled: task.ReminderEnabled, AdvanceMinutes: task.AdvanceMinutes).Validate();
            Require(task.OriginalDate.Year is >= 1900 and <= 9998, "实例原日期无效。");
            Require(task.Progress is >= 0 and <= 100 && (task.Progress == 100) == task.CompletedAt.HasValue, "进度或完成时间不一致。");
        }
        foreach (var e in d.ProgressEvents)
            Require(occurrences.Contains(e.OccurrenceId) && e.OldProgress is >= 0 and <= 100 && e.NewProgress is >= 0 and <= 100, "进度事件无效。");
        Require(d.Reviews.Select(r => (r.Period, r.PeriodStart)).Distinct().Count() == d.Reviews.Count, "重复的总结。");
        foreach (var r in d.Reviews)
            Require(Enum.IsDefined(r.Period) && r.PeriodStart.Year is >= 1900 and <= 9998 && r.Content is not null
                && CalendarRange.For(r.Period, r.PeriodStart).Start == r.PeriodStart, "总结周期无效。");
        var s = d.Settings;
        Require(Enum.IsDefined(s.Mode) && Enum.IsDefined(s.Theme), "模式或主题无效。");
        Require(double.IsFinite(s.Left) && double.IsFinite(s.Top) && s.Width is >= 320 and <= 10000
            && s.Height is >= 420 and <= 10000 && s.BackgroundOpacity is >= 0.35 and <= 1 && s.FontSize is >= 11 and <= 20, "窗口设置无效。");
    }
    public static void ExportCsv(IEnumerable<TaskOccurrence> source, string path, DateOnly start, DateOnly end)
    {
        var tasks = source.Where(t => !t.Deleted && t.ScheduledDate >= start && t.ScheduledDate <= end)
            .OrderBy(t => t.ScheduledDate).ThenBy(t => t.StartTime ?? TimeOnly.MaxValue).ToList();
        var stats = Statistics.For(tasks);
        var lines = new List<string>
        {
            "开始日期,结束日期,计划总数,已完成,未完成,完成率(%),平均进度(%)",
            $"{start:yyyy-MM-dd},{end:yyyy-MM-dd},{stats.Total},{stats.Completed},{stats.Incomplete},{Number(stats.CompletionRate)},{Number(stats.AverageProgress)}",
            "", "日期,开始时间,结束时间,标题,进度(%),状态,完成时间(含时区),备注,任务ID"
        };
        foreach (var t in tasks)
            lines.Add(string.Join(',', new[] { PlannerStore.Day(t.ScheduledDate), t.StartTime?.ToString("HH:mm") ?? "", t.EndTime?.ToString("HH:mm") ?? "",
                t.Title, t.Progress.ToString(CultureInfo.InvariantCulture), t.IsCompleted ? "已完成" : t.Progress == 0 ? "未开始" : "进行中",
                t.CompletedAt?.ToLocalTime().ToString("O") ?? "", t.Note, t.Id.ToString() }.Select(Csv)));
        AtomicWrite(path, string.Join("\r\n", lines), true);
    }
    private static string Number(double? value) => value?.ToString("0.##", CultureInfo.InvariantCulture) ?? "";
    private static string Csv(string text)
    {
        // Spreadsheet applications must not execute titles/notes as formulas.
        if (text.TrimStart().StartsWith('=') || text.TrimStart().StartsWith('+') || text.TrimStart().StartsWith('-') || text.TrimStart().StartsWith('@')
            || text.StartsWith('\t') || text.StartsWith('\r')) text = "'" + text;
        return "\"" + text.Replace("\"", "\"\"") + "\"";
    }
    public static void AtomicWrite(string path, string content, bool bom = false)
    {
        var fullPath = Path.GetFullPath(path);
        var temp = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            using (var writer = new StreamWriter(file, new UTF8Encoding(bom)))
            { writer.Write(content); writer.Flush(); file.Flush(true); }
            File.Move(temp, fullPath, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
