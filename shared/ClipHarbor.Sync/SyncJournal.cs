using Microsoft.Data.Sqlite;

namespace ClipHarbor.Sync;

public sealed record LocalAttachment(string Path, string Kind, string Name, long ByteLength, string Sha256);
public sealed record PendingOperation(SyncOperation Operation, List<LocalAttachment> Attachments, string? SourceId = null);
public sealed record ReceivedRecord(WireRecord Record, string? ImagePath, List<string> FilePaths);

public sealed class SyncJournal
{
    private readonly object _gate = new();
    private readonly string _connection;
    public string DirectoryPath { get; }
    public SyncJournal(string directory)
    {
        DirectoryPath = directory; Directory.CreateDirectory(directory);
        _connection = new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "sync.sqlite"), Mode = SqliteOpenMode.ReadWriteCreate }.ToString();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS state(key TEXT PRIMARY KEY,value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS mirror(id TEXT PRIMARY KEY,data TEXT NOT NULL,hidden INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS aliases(id TEXT PRIMARY KEY,canonical TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS outbox(seq INTEGER PRIMARY KEY AUTOINCREMENT,id TEXT UNIQUE NOT NULL,data TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS failures(id TEXT PRIMARY KEY,data TEXT NOT NULL,reason TEXT NOT NULL);
            """;
        command.ExecuteNonQuery();
    }
    private SqliteConnection Open() { var connection = new SqliteConnection(_connection); connection.Open(); return connection; }
    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string, object?)[] parameters)
    {
        var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }
    public string GetState(string key, string fallback = "")
    {
        lock (_gate) { using var connection = Open(); using var command = Command(connection, null, "SELECT value FROM state WHERE key=$key", ("$key", key)); return command.ExecuteScalar() as string ?? fallback; }
    }
    public void SetState(string key, string value)
    {
        lock (_gate) { using var connection = Open(); using var command = Command(connection, null, "INSERT INTO state(key,value) VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=$value", ("$key", key), ("$value", value)); command.ExecuteNonQuery(); }
    }
    public string Resolve(string id)
    {
        lock (_gate) { using var connection = Open(); using var command = Command(connection, null, "SELECT canonical FROM aliases WHERE id=$id", ("$id", id)); return command.ExecuteScalar() as string ?? id; }
    }
    public WireRecord? Record(string id)
    {
        lock (_gate) { using var connection = Open(); using var command = Command(connection, null, "SELECT data FROM mirror WHERE id=$id", ("$id", Resolve(id))); return command.ExecuteScalar() is string data ? Protocol.Decode<WireRecord>(data) : null; }
    }
    public bool Hidden(string id)
    {
        lock (_gate) { using var connection = Open(); using var command = Command(connection, null, "SELECT hidden FROM mirror WHERE id=$id", ("$id", Resolve(id))); return Convert.ToInt32(command.ExecuteScalar() ?? 0) != 0; }
    }
    public void Hide(string id)
    {
        lock (_gate) { using var connection = Open(); using var command = Command(connection, null, "UPDATE mirror SET hidden=1 WHERE id=$id", ("$id", Resolve(id))); command.ExecuteNonQuery(); }
    }
    public List<WireRecord> Records(bool includeHidden = false)
    {
        lock (_gate)
        {
            using var connection = Open(); using var command = Command(connection, null, includeHidden ? "SELECT data FROM mirror" : "SELECT data FROM mirror WHERE hidden=0");
            using var reader = command.ExecuteReader(); List<WireRecord> records = []; while (reader.Read()) records.Add(Protocol.Decode<WireRecord>(reader.GetString(0))); return records;
        }
    }
    public void Enqueue(PendingOperation operation)
    {
        lock (_gate)
        {
            using var connection = Open(); using var transaction = connection.BeginTransaction();
            if (operation.Operation.Record is { } record)
            {
                using var projection = Command(connection, transaction, "INSERT INTO mirror(id,data) VALUES($id,$data) ON CONFLICT(id) DO NOTHING", ("$id", record.RecordId), ("$data", Protocol.Encode(record))); projection.ExecuteNonQuery();
            }
            using var command = Command(connection, transaction, "INSERT INTO outbox(id,data) VALUES($id,$data) ON CONFLICT(id) DO NOTHING", ("$id", operation.Operation.OperationId), ("$data", Protocol.Encode(operation))); command.ExecuteNonQuery(); transaction.Commit();
        }
    }
    public List<PendingOperation> Pending()
    {
        lock (_gate)
        {
            using var connection = Open(); using var command = Command(connection, null, "SELECT data FROM outbox ORDER BY seq"); using var reader = command.ExecuteReader(); List<PendingOperation> operations = [];
            while (reader.Read()) operations.Add(Protocol.Decode<PendingOperation>(reader.GetString(0))); return operations;
        }
    }
    public void UpdatePending(PendingOperation operation)
    {
        lock (_gate) { using var connection = Open(); using var command = Command(connection, null, "UPDATE outbox SET data=$data WHERE id=$id", ("$data", Protocol.Encode(operation)), ("$id", operation.Operation.OperationId)); command.ExecuteNonQuery(); }
    }
    public void ApplyPage(List<WireRecord> records, string cursor, string epoch)
    {
        lock (_gate)
        {
            using var connection = Open(); using var transaction = connection.BeginTransaction();
            foreach (var record in records) Upsert(connection, transaction, record);
            using var command = Command(connection, transaction, "INSERT INTO state(key,value) VALUES('cursor',$cursor),('epoch',$epoch) ON CONFLICT(key) DO UPDATE SET value=excluded.value", ("$cursor", cursor), ("$epoch", epoch)); command.ExecuteNonQuery(); transaction.Commit();
        }
    }
    private static void Upsert(SqliteConnection connection, SqliteTransaction transaction, WireRecord record)
    {
        using var command = Command(connection, transaction, "INSERT INTO mirror(id,data) VALUES($id,$data) ON CONFLICT(id) DO UPDATE SET data=$data WHERE CAST(json_extract(mirror.data,'$.revision') AS INTEGER)<=CAST(json_extract($data,'$.revision') AS INTEGER)", ("$id", record.RecordId), ("$data", Protocol.Encode(record))); command.ExecuteNonQuery();
    }
    public void Acknowledge(PendingOperation pending, OperationResult result)
    {
        lock (_gate)
        {
            using var connection = Open(); using var transaction = connection.BeginTransaction();
            if (result.Record is { } record)
            {
                if (pending.Operation.RecordId != record.RecordId)
                {
                    using var alias = Command(connection, transaction, "INSERT INTO aliases(id,canonical) VALUES($id,$canonical) ON CONFLICT(id) DO UPDATE SET canonical=$canonical", ("$id", pending.Operation.RecordId), ("$canonical", record.RecordId)); alias.ExecuteNonQuery();
                    using var migrate = Command(connection, transaction, "INSERT INTO mirror(id,data,hidden) SELECT $canonical,$data,hidden FROM mirror WHERE id=$id ON CONFLICT(id) DO UPDATE SET hidden=max(hidden,excluded.hidden); DELETE FROM mirror WHERE id=$id", ("$id", pending.Operation.RecordId), ("$canonical", record.RecordId), ("$data", Protocol.Encode(record))); migrate.ExecuteNonQuery();
                }
                Upsert(connection, transaction, record);
            }
            if (result.Status is "conflict" or "rejected" || (result.Status == "deleted" && pending.Operation.Type == "note"))
            {
                using var failure = Command(connection, transaction, "INSERT OR REPLACE INTO failures(id,data,reason) VALUES($id,$data,$reason)", ("$id", pending.Operation.OperationId), ("$data", Protocol.Encode(pending)), ("$reason", result.Code ?? result.Status)); failure.ExecuteNonQuery();
            }
            using var remove = Command(connection, transaction, "DELETE FROM outbox WHERE id=$id", ("$id", pending.Operation.OperationId)); remove.ExecuteNonQuery(); transaction.Commit();
        }
    }
    public string ExportFailures()
    {
        lock (_gate)
        {
            using var connection = Open(); using var command = Command(connection, null, "SELECT data,reason FROM failures"); using var reader = command.ExecuteReader(); List<object> failures = [];
            while (reader.Read()) failures.Add(new { operation = Protocol.Decode<PendingOperation>(reader.GetString(0)), reason = reader.GetString(1) });
            var path = Path.Combine(DirectoryPath, "sync-conflicts.json"); File.WriteAllText(path, Protocol.Encode(failures)); return path;
        }
    }
}
