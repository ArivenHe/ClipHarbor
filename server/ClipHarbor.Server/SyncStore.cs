using ClipHarbor.Sync;
using Npgsql;

namespace ClipHarbor.Server;

public sealed class SyncStore(Database database)
{
    public async Task<WireRecord?> Record(NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid account, string id)
    {
        if (!Guid.TryParse(id, out var recordId)) throw new ApiException(400, "INVALID_ID", "记录 ID 无效。");
        var alias = await Database.Scalar(connection, transaction, "SELECT record_id FROM aliases WHERE account_id=$1 AND alias_id=$2", account, recordId);
        if (alias is Guid canonical) recordId = canonical;
        var data = await Database.Scalar(connection, transaction, "SELECT data::text FROM records WHERE account_id=$1 AND id=$2", account, recordId);
        return data is string json ? Protocol.Decode<WireRecord>(json) : null;
    }
    public async Task<OperationsResponse> Apply(AuthSession session, OperationsRequest request)
    {
        if (request.Operations is null || request.Operations.Count is < 1 or > 100 || request.Operations.Any(op => op is null)) throw new ApiException(400, "INVALID_BATCH", "一次最多提交 100 个有效操作。");
        await using var connection = await database.Open();
        await using var transaction = await connection.BeginTransactionAsync();
        var state = await Database.LockAccount(connection, transaction, session.AccountId);
        if (state.Epoch != request.SyncEpoch) throw new ApiException(409, "EPOCH_CHANGED", "服务器数据空间已变化，请重新连接并恢复历史。");
        var sequence = state.History;
        List<OperationResult> results = [];
        foreach (var operation in request.Operations)
        {
            if (!Guid.TryParse(operation.OperationId, out var operationId)) throw new ApiException(400, "INVALID_ID", "操作 ID 无效。");
            var previous = await Database.Scalar(connection, transaction, "SELECT result::text FROM operations WHERE account_id=$1 AND id=$2", session.AccountId, operationId);
            if (previous is string cached) { results.Add(Protocol.Decode<OperationResult>(cached)); continue; }
            await transaction.SaveAsync("current_operation");
            var before = sequence;
            OperationResult result;
            try
            {
                var existing = await Record(connection, transaction, session.AccountId, operation.RecordId);
                if (existing?.DeletedAt is not null) result = new(operation.OperationId, "deleted", existing, "RECORD_DELETED");
                else if (operation.Type is "create" or "capture")
                {
                    var record = operation.Record ?? throw new InvalidDataException("创建操作缺少记录。");
                    record.Validate();
                    if (DateTimeOffset.Parse(record.CapturedAt) > DateTimeOffset.UtcNow.AddMinutes(5)) record.CapturedAt = Protocol.Now;
                    if (operation.Type == "capture" || DateTimeOffset.Parse(record.LastCapturedAt) > DateTimeOffset.UtcNow.AddMinutes(5)) record.LastCapturedAt = Protocol.Now;
                    if (record.RecordId != operation.RecordId) throw new InvalidDataException("记录 ID 不一致。");
                    await ValidateAttachments(connection, transaction, session.AccountId, record);
                    if (existing is null)
                    {
                        var duplicate = await Database.Scalar(connection, transaction, "SELECT data::text FROM records WHERE account_id=$1 AND content_hash=$2 AND NOT deleted", session.AccountId, record.ContentHash);
                        if (duplicate is string json) existing = Protocol.Decode<WireRecord>(json);
                    }
                    if (existing is not null)
                    {
                        if (existing.ContentHash != record.ContentHash) throw new InvalidDataException("已有记录的正文不能改变。");
                        await Database.Execute(connection, transaction, "INSERT INTO aliases(account_id,alias_id,record_id) VALUES($1,$2,$3) ON CONFLICT(account_id,alias_id) DO NOTHING", session.AccountId, Guid.Parse(operation.RecordId), Guid.Parse(existing.RecordId));
                        if (operation.Type == "capture") { existing.LastCapturedAt = record.LastCapturedAt; existing.OriginDeviceId = session.DeviceId.ToString(); await Write(connection, transaction, session.AccountId, existing, ++sequence); }
                        result = new(operation.OperationId, "accepted", existing);
                    }
                    else
                    {
                        var count = Convert.ToInt64(await Database.Scalar(connection, transaction, "SELECT count(*) FROM records WHERE account_id=$1 AND NOT deleted", session.AccountId));
                        if (count >= 10_000) throw new ApiException(409, "QUOTA_EXCEEDED", "云端历史数量已满。");
                        var used = Convert.ToInt64(await Database.Scalar(connection, transaction, "SELECT (SELECT coalesce(sum(byte_length),0) FROM uploads WHERE account_id=$1)+(SELECT coalesce(sum(octet_length(data::text)),0) FROM records WHERE account_id=$1 AND NOT deleted)", session.AccountId));
                        if (used + System.Text.Encoding.UTF8.GetByteCount(Protocol.Encode(record)) > Protocol.QuotaBytes) throw new ApiException(409, "QUOTA_EXCEEDED", "账号存储容量已满。");
                        record.OriginDeviceId = session.DeviceId.ToString(); record.DeletedAt = null;
                        record.FavoriteRevision = record.NoteRevision = sequence + 1;
                        await Write(connection, transaction, session.AccountId, record, ++sequence);
                        await Database.Execute(connection, transaction, "INSERT INTO aliases(account_id,alias_id,record_id) VALUES($1,$2,$2) ON CONFLICT DO NOTHING", session.AccountId, Guid.Parse(record.RecordId));
                        result = new(operation.OperationId, "accepted", record);
                    }
                }
                else if (existing is null) result = new(operation.OperationId, "rejected", null, "RECORD_NOT_FOUND");
                else if (operation.Type == "delete")
                {
                    existing.DeletedAt = Protocol.Now;
                    await Write(connection, transaction, session.AccountId, existing, ++sequence);
                    result = new(operation.OperationId, "accepted", existing);
                }
                else if (operation.Type is "favorite" or "note")
                {
                    var revision = operation.Type == "favorite" ? existing.FavoriteRevision : existing.NoteRevision;
                    if (operation.ExpectedRevision != revision) result = new(operation.OperationId, "conflict", existing, "FIELD_CONFLICT");
                    else
                    {
                        if (operation.Type == "favorite") { existing.Favorite = operation.Favorite ?? throw new InvalidDataException("收藏操作缺少值。"); existing.FavoriteRevision = sequence + 1; }
                        else { existing.Note = operation.Note ?? ""; if (existing.Note.Length > 100_000) throw new InvalidDataException("备注过长。"); existing.NoteRevision = sequence + 1; }
                        await Write(connection, transaction, session.AccountId, existing, ++sequence);
                        result = new(operation.OperationId, "accepted", existing);
                    }
                }
                else throw new InvalidDataException("操作类型无效。");
            }
            catch (Exception error) when (error is ApiException or InvalidDataException or FormatException)
            {
                await transaction.RollbackAsync("current_operation"); sequence = before;
                result = new(operation.OperationId, "rejected", null, error is ApiException api ? api.Code : "INVALID_RECORD");
            }
            await Database.Execute(connection, transaction, "INSERT INTO operations(account_id,id,result) VALUES($1,$2,$3::jsonb)", session.AccountId, operationId, Protocol.Encode(result));
            results.Add(result);
        }
        await Database.Execute(connection, transaction, "UPDATE accounts SET history_sequence=$2 WHERE id=$1", session.AccountId, sequence);
        await transaction.CommitAsync();
        return new(results, sequence.ToString());
    }
    private static async Task ValidateAttachments(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid account, WireRecord record)
    {
        IEnumerable<WireFile> files = record.Kind == "image" ? new[] { new WireFile("image.png", -1, record.BlobSha256!, record.BlobId!) } : record.Files;
        foreach (var file in files)
        {
            await using var command = Database.Command(connection, transaction, "SELECT byte_length,sha256,kind FROM uploads WHERE id=$1 AND account_id=$2 AND completed", Guid.Parse(file.BlobId), account);
            await using var reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync() || reader.GetString(1) != file.Sha256 || (file.ByteLength >= 0 && reader.GetInt64(0) != file.ByteLength) || (record.Kind == "image" && reader.GetString(2) != "image")) throw new ApiException(400, "ATTACHMENT_INVALID", "附件未完成或不属于此账号。");
        }
        foreach (var file in files) await Database.Execute(connection, transaction, "UPDATE uploads SET consumed=true WHERE id=$1 AND account_id=$2", Guid.Parse(file.BlobId), account);
    }
    internal static async Task Write(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid account, WireRecord record, long sequence)
    {
        record.Revision = sequence;
        var json = Protocol.Encode(record);
        await Database.Execute(connection, transaction, "INSERT INTO records(account_id,id,content_hash,deleted,data) VALUES($1,$2,$3,$4,$5::jsonb) ON CONFLICT(account_id,id) DO UPDATE SET data=$5::jsonb,deleted=$4,touched_at=now()", account, Guid.Parse(record.RecordId), record.ContentHash, record.DeletedAt is not null, json);
        await Database.Execute(connection, transaction, "INSERT INTO changes(account_id,sequence,data) VALUES($1,$2,$3::jsonb)", account, sequence, json);
    }
    public async Task<ChangesResponse> Changes(AuthSession session, string cursor, int limit)
    {
        if (!long.TryParse(cursor, out var after) || after < 0) throw new ApiException(400, "INVALID_CURSOR", "同步游标无效。");
        limit = Math.Clamp(limit, 1, 100);
        await using var connection = await database.Open();
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead);
        long high; string epoch;
        await using (var command = Database.Command(connection, transaction, "SELECT history_sequence,sync_epoch::text FROM accounts WHERE id=$1", session.AccountId))
        await using (var reader = await command.ExecuteReaderAsync()) { await reader.ReadAsync(); high = reader.GetInt64(0); epoch = reader.GetString(1); }
        if (after > high) throw new ApiException(409, "EPOCH_CHANGED", "同步数据已重建。");
        var earliest = Convert.ToInt64(await Database.Scalar(connection, transaction, "SELECT coalesce(min(sequence),0) FROM changes WHERE account_id=$1", session.AccountId));
        if (after < earliest - 1) throw new ApiException(410, "CURSOR_EXPIRED", "同步游标已过期。");
        List<WireRecord> records = [];
        await using (var command = Database.Command(connection, transaction, "SELECT data::text FROM changes WHERE account_id=$1 AND sequence>$2 AND sequence<=$3 ORDER BY sequence LIMIT $4", session.AccountId, after, high, limit))
        await using (var reader = await command.ExecuteReaderAsync()) while (await reader.ReadAsync()) records.Add(Protocol.Decode<WireRecord>(reader.GetString(0)));
        var next = records.Count == 0 ? high : records[^1].Revision;
        await transaction.CommitAsync();
        return new(records, next.ToString(), high.ToString(), next < high, epoch);
    }
    public async Task<SnapshotResponse> Snapshot(AuthSession session, string? pageToken)
    {
        await using var connection = await database.Open();
        Guid id; int offset = 0; long cursor; string epoch; List<WireRecord> records;
        if (string.IsNullOrEmpty(pageToken))
        {
            id = Guid.NewGuid();
            await using var transaction = await connection.BeginTransactionAsync();
            var state = await Database.LockAccount(connection, transaction, session.AccountId); cursor = state.History; epoch = state.Epoch;
            var json = (string?)await Database.Scalar(connection, transaction, "SELECT coalesce(jsonb_agg(data ORDER BY (data->>'revision')::bigint),'[]'::jsonb)::text FROM records WHERE account_id=$1", session.AccountId) ?? "[]";
            records = Protocol.Decode<List<WireRecord>>(json);
            await Database.Execute(connection, transaction, "INSERT INTO snapshots(id,account_id,cursor,epoch,records) VALUES($1,$2,$3,$4,$5::jsonb)", id, session.AccountId, cursor, Guid.Parse(epoch), json);
            await transaction.CommitAsync();
        }
        else
        {
            var parts = pageToken.Split(':');
            if (parts.Length != 2 || !Guid.TryParse(parts[0], out id) || !int.TryParse(parts[1], out offset) || offset < 0) throw new ApiException(400, "INVALID_CURSOR", "快照游标无效。");
            await using var command = Database.Command(connection, null, "SELECT cursor,epoch::text,records::text FROM snapshots WHERE id=$1 AND account_id=$2 AND expires_at>now()", id, session.AccountId);
            await using var reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync()) throw new ApiException(410, "SNAPSHOT_EXPIRED", "请重新下载快照。");
            cursor = reader.GetInt64(0); epoch = reader.GetString(1); records = Protocol.Decode<List<WireRecord>>(reader.GetString(2));
        }
        var page = records.Skip(offset).Take(100).ToList();
        return new(page, offset + page.Count < records.Count ? $"{id}:{offset + page.Count}" : null, cursor.ToString(), epoch);
    }
    public async Task<ClipboardLatest> Latest(AuthSession session)
    {
        await using var connection = await database.Open();
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead);
        var sequence = (long)(await Database.Scalar(connection, transaction, "SELECT clipboard_sequence FROM accounts WHERE id=$1", session.AccountId))!;
        ClipboardEvent? latest = null;
        await using (var command = Database.Command(connection, transaction, "SELECT id::text,sequence,device_id::text,record_id::text,kind,created_at FROM clipboard_events WHERE account_id=$1 ORDER BY sequence DESC LIMIT 1", session.AccountId))
        await using (var reader = await command.ExecuteReaderAsync())
            if (await reader.ReadAsync()) latest = new(reader.GetString(0), reader.GetInt64(1).ToString(), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetDateTime(5).ToString("O"));
        var record = latest is null ? null : await Record(connection, transaction, session.AccountId, latest.RecordId);
        if (record?.DeletedAt is not null) { latest = null; record = null; }
        await transaction.CommitAsync();
        return new(sequence.ToString(), latest, record, session.Epoch);
    }
    public async Task<ClipboardLatest> Publish(AuthSession session, ClipboardPublish request)
    {
        if (!Guid.TryParse(request.ClipboardEventId, out var id)) throw new ApiException(400, "INVALID_ID", "复制事件 ID 无效。");
        await using var connection = await database.Open();
        await using var transaction = await connection.BeginTransactionAsync();
        var state = await Database.LockAccount(connection, transaction, session.AccountId);
        if (state.Epoch != request.SyncEpoch) throw new ApiException(409, "EPOCH_CHANGED", "服务器数据空间已变化。");
        var record = await Record(connection, transaction, session.AccountId, request.RecordId);
        if (record is null) throw new ApiException(404, "RECORD_NOT_FOUND", "复制内容不存在。");
        if (record.DeletedAt is not null) throw new ApiException(410, "RECORD_DELETED", "复制内容不可用。");
        var existing = await Database.Scalar(connection, transaction, "SELECT sequence FROM clipboard_events WHERE account_id=$1 AND id=$2", session.AccountId, id);
        var sequence = existing is long previous ? previous : state.Clipboard + 1;
        if (existing is null)
        {
            await Database.Execute(connection, transaction, "INSERT INTO clipboard_events(account_id,id,sequence,record_id,device_id,kind) VALUES($1,$2,$3,$4,$5,$6)", session.AccountId, id, sequence, Guid.Parse(record.RecordId), session.DeviceId, record.Kind);
            await Database.Execute(connection, transaction, "UPDATE accounts SET clipboard_sequence=$2 WHERE id=$1", session.AccountId, sequence);
        }
        await transaction.CommitAsync();
        return await Latest(session);
    }
    public async Task Clear(AuthSession session, string epoch, string operationId, long before, bool keepFavorites)
    {
        if (!Guid.TryParse(operationId, out var id) || before < 0) throw new ApiException(400, "INVALID_CLEAR", "清空范围无效。");
        await using var connection = await database.Open();
        await using var transaction = await connection.BeginTransactionAsync();
        var state = await Database.LockAccount(connection, transaction, session.AccountId);
        if (state.Epoch != epoch || before > state.History) throw new ApiException(409, "EPOCH_CHANGED", "清空范围已变化。");
        if (await Database.Scalar(connection, transaction, "SELECT 1 FROM operations WHERE account_id=$1 AND id=$2", session.AccountId, id) is not null) return;
        var sequence = state.History;
        List<WireRecord> records = [];
        await using (var command = Database.Command(connection, transaction, "SELECT data::text FROM records WHERE account_id=$1 AND NOT deleted AND (data->>'revision')::bigint<=$2 AND (NOT $3 OR NOT (data->>'favorite')::boolean)", session.AccountId, before, keepFavorites))
        await using (var reader = await command.ExecuteReaderAsync()) while (await reader.ReadAsync()) records.Add(Protocol.Decode<WireRecord>(reader.GetString(0)));
        foreach (var record in records) { record.DeletedAt = Protocol.Now; await Write(connection, transaction, session.AccountId, record, ++sequence); }
        await Database.Execute(connection, transaction, "UPDATE accounts SET history_sequence=$2 WHERE id=$1", session.AccountId, sequence);
        await Database.Execute(connection, transaction, "INSERT INTO operations(account_id,id,result) VALUES($1,$2,$3::jsonb)", session.AccountId, id, Protocol.Encode(new OperationResult(operationId, "accepted")));
        await transaction.CommitAsync();
    }
}
