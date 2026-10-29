using System.Globalization;
using System.Text.Json;
using DeskPlan.Core;
using Microsoft.Data.Sqlite;

namespace DeskPlan.Infrastructure;

/// <summary>One UI-thread-owned connection; every logical mutation is an atomic transaction.</summary>
public sealed class PlannerStore : IDisposable
{
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Create(System.Text.Unicode.UnicodeRanges.All) };
    private readonly SqliteConnection connection;
    private SqliteTransaction? transaction;
    public string DatabasePath { get; }

    public PlannerStore(string databasePath)
    {
        DatabasePath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        var existed = File.Exists(DatabasePath);
        connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath, ForeignKeys = true, DefaultTimeout = 10, Pooling = false
        }.ToString());
        connection.Open();
        var version = Convert.ToInt32(Scalar("PRAGMA user_version;"));
        if (version > 1) { connection.Dispose(); throw new InvalidDataException("数据库由更高版本的 DeskPlan 创建，请更新软件。原数据未被改动。"); }
        if (version == 0)
        {
            if (existed) CreateDatabaseBackup("before-migration");
            Transaction(() =>
            {
                Execute("""
                    CREATE TABLE IF NOT EXISTS TaskDefinitions (id TEXT PRIMARY KEY, data TEXT NOT NULL);
                    CREATE TABLE IF NOT EXISTS TaskOccurrences (
                        id TEXT PRIMARY KEY, definition_id TEXT NOT NULL REFERENCES TaskDefinitions(id),
                        original_date TEXT NOT NULL, scheduled_date TEXT NOT NULL,
                        progress INTEGER NOT NULL CHECK(progress BETWEEN 0 AND 100),
                        deleted INTEGER NOT NULL CHECK(deleted IN (0,1)), data TEXT NOT NULL,
                        UNIQUE(definition_id, original_date));
                    CREATE INDEX IF NOT EXISTS ix_occurrence_date ON TaskOccurrences(scheduled_date, deleted);
                    CREATE TABLE IF NOT EXISTS ProgressEvents (
                        id TEXT PRIMARY KEY, occurrence_id TEXT NOT NULL REFERENCES TaskOccurrences(id), data TEXT NOT NULL);
                    CREATE TABLE IF NOT EXISTS AuditEvents (id TEXT PRIMARY KEY, data TEXT NOT NULL);
                    CREATE TABLE IF NOT EXISTS ReviewNotes (
                        id TEXT PRIMARY KEY, period INTEGER NOT NULL, period_start TEXT NOT NULL, data TEXT NOT NULL,
                        UNIQUE(period, period_start));
                    CREATE TABLE IF NOT EXISTS AppSettings (id INTEGER PRIMARY KEY CHECK(id=1), data TEXT NOT NULL);
                    PRAGMA user_version=1;
                    """);
            });
        }
        Execute("PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA busy_timeout=10000;");
        var integrity = Convert.ToString(Scalar("PRAGMA quick_check;"));
        if (integrity != "ok") { connection.Dispose(); throw new InvalidDataException("数据库完整性检查失败，请保留原文件并从备份恢复。"); }
    }

    private SqliteCommand Command(string sql, params (string Name, object? Value)[] args)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        foreach (var (name, value) in args) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }
    private void Execute(string sql, params (string Name, object? Value)[] args) { using var cmd = Command(sql, args); cmd.ExecuteNonQuery(); }
    private object? Scalar(string sql) { using var cmd = Command(sql); return cmd.ExecuteScalar(); }
    private List<T> Query<T>(string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = Command(sql, args);
        using var reader = cmd.ExecuteReader();
        var result = new List<T>();
        while (reader.Read()) result.Add(JsonSerializer.Deserialize<T>(reader.GetString(0), Json)
            ?? throw new InvalidDataException("数据库包含无法读取的记录，请保留文件并恢复备份。"));
        return result;
    }
    public void Transaction(Action action)
    {
        if (transaction is not null) throw new InvalidOperationException("Nested transactions are not supported.");
        using var tx = connection.BeginTransaction();
        transaction = tx;
        try { action(); tx.Commit(); }
        catch { tx.Rollback(); throw; }
        finally { transaction = null; }
    }
    public List<TaskDefinition> Definitions() => Query<TaskDefinition>("SELECT data FROM TaskDefinitions");
    public List<TaskOccurrence> AllOccurrences() => Query<TaskOccurrence>("SELECT data FROM TaskOccurrences");
    public List<TaskOccurrence> Occurrences(DateOnly start, DateOnly end, bool includeDeleted = false) => Query<TaskOccurrence>(
        "SELECT data FROM TaskOccurrences WHERE scheduled_date >= $start AND scheduled_date <= $end" +
        (includeDeleted ? "" : " AND deleted=0"), ("$start", Day(start)), ("$end", Day(end)));
    public TaskOccurrence? Find(Guid id) => Query<TaskOccurrence>("SELECT data FROM TaskOccurrences WHERE id=$id", ("$id", id.ToString())).SingleOrDefault();
    public List<ProgressEvent> ProgressEvents() => Query<ProgressEvent>("SELECT data FROM ProgressEvents");
    public List<AuditEvent> AuditEvents() => Query<AuditEvent>("SELECT data FROM AuditEvents");
    public List<ReviewNote> Reviews() => Query<ReviewNote>("SELECT data FROM ReviewNotes");
    public PlannerSettings Settings() => Query<PlannerSettings>("SELECT data FROM AppSettings WHERE id=1").SingleOrDefault() ?? new();
    public void Save(TaskDefinition item) => Execute("INSERT INTO TaskDefinitions(id,data) VALUES($id,$data) ON CONFLICT(id) DO UPDATE SET data=$data",
        ("$id", item.Id.ToString()), ("$data", Encode(item)));
    public void Save(TaskOccurrence item) => Execute("""
        INSERT INTO TaskOccurrences(id,definition_id,original_date,scheduled_date,progress,deleted,data)
        VALUES($id,$def,$original,$date,$progress,$deleted,$data)
        ON CONFLICT(id) DO UPDATE SET definition_id=$def,original_date=$original,scheduled_date=$date,progress=$progress,deleted=$deleted,data=$data
        """, ("$id", item.Id.ToString()), ("$def", item.DefinitionId.ToString()), ("$original", Day(item.OriginalDate)),
        ("$date", Day(item.ScheduledDate)), ("$progress", item.Progress), ("$deleted", item.Deleted ? 1 : 0), ("$data", Encode(item)));
    public void Save(ProgressEvent item) => Execute("INSERT INTO ProgressEvents(id,occurrence_id,data) VALUES($id,$occ,$data)",
        ("$id", item.Id.ToString()), ("$occ", item.OccurrenceId.ToString()), ("$data", Encode(item)));
    public void Save(AuditEvent item) => Execute("INSERT INTO AuditEvents(id,data) VALUES($id,$data)", ("$id", item.Id.ToString()), ("$data", Encode(item)));
    public void Save(ReviewNote item) => Execute("""
        INSERT INTO ReviewNotes(id,period,period_start,data) VALUES($id,$period,$start,$data)
        ON CONFLICT(period,period_start) DO UPDATE SET id=$id,data=$data
        """, ("$id", item.Id.ToString()), ("$period", (int)item.Period), ("$start", Day(item.PeriodStart)), ("$data", Encode(item)));
    public void Save(PlannerSettings item) => Execute("INSERT INTO AppSettings(id,data) VALUES(1,$data) ON CONFLICT(id) DO UPDATE SET data=$data", ("$data", Encode(item)));
    public static string Encode<T>(T value) => JsonSerializer.Serialize(value, Json);
    public static string Day(DateOnly value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public string CreateDatabaseBackup(string reason)
    {
        var folder = Path.Combine(Path.GetDirectoryName(DatabasePath)!, "backups");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"deskplan-{DateTime.Now:yyyyMMdd-HHmmss-fff}-{reason}.db");
        using var backup = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        backup.Open();
        connection.BackupDatabase(backup);
        return path;
    }

    public BackupDocument Snapshot() => new()
    {
        Definitions = Definitions(), Occurrences = AllOccurrences(), ProgressEvents = ProgressEvents(),
        AuditEvents = AuditEvents(), Reviews = Reviews(), Settings = Settings()
    };

    public string Restore(BackupDocument document)
    {
        BackupService.Validate(document);
        var backup = CreateDatabaseBackup("before-restore");
        Transaction(() =>
        {
            Execute("DELETE FROM ProgressEvents; DELETE FROM AuditEvents; DELETE FROM TaskOccurrences; DELETE FROM TaskDefinitions; DELETE FROM ReviewNotes; DELETE FROM AppSettings;");
            foreach (var item in document.Definitions) Save(item);
            foreach (var item in document.Occurrences) Save(item);
            foreach (var item in document.ProgressEvents) Save(item);
            foreach (var item in document.AuditEvents) Save(item);
            foreach (var item in document.Reviews) Save(item);
            Save(document.Settings);
            Save(new AuditEvent(Guid.NewGuid(), null, "恢复备份", "", document.ExportedAt.ToString("O"), DateTimeOffset.UtcNow));
        });
        return backup;
    }
    public void Dispose() => connection.Dispose();
}
