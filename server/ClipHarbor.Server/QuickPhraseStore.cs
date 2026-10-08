using ClipHarbor.Sync;
using Npgsql;

namespace ClipHarbor.Server;

public sealed class QuickPhraseStore(Database database)
{
    private static string Table(string kind) => kind switch { "phrase" => "quick_phrases", "group" => "quick_phrase_groups", "preference" => "quick_phrase_preset_preferences", _ => throw new InvalidDataException("短语实体类型无效。") };
    private const string All = "SELECT data FROM quick_phrases WHERE account_id=$1 UNION ALL SELECT data FROM quick_phrase_groups WHERE account_id=$1 UNION ALL SELECT data FROM quick_phrase_preset_preferences WHERE account_id=$1";
    private static async Task<PhraseEntity?> Read(NpgsqlConnection c, NpgsqlTransaction? t, Guid account, string kind, string id)
        => await Database.Scalar(c, t, $"SELECT data::text FROM {Table(kind)} WHERE account_id=$1 AND id=$2", account, id) is string json ? Protocol.Decode<PhraseEntity>(json) : null;
    private static async Task Write(NpgsqlConnection c, NpgsqlTransaction t, Guid account, PhraseEntity entity, long sequence)
    {
        entity.Revision = sequence.ToString(); entity.UpdatedAt = Protocol.Now;
        var json = Protocol.Encode(entity);
        await Database.Execute(c, t, $"INSERT INTO {Table(entity.Kind)}(account_id,id,deleted,data) VALUES($1,$2,$3,$4::jsonb) ON CONFLICT(account_id,id) DO UPDATE SET deleted=$3,data=$4::jsonb", account, entity.Id, entity.DeletedAt is not null, json);
        await Database.Execute(c, t, "INSERT INTO quick_phrase_changes(account_id,sequence,data) VALUES($1,$2,$3::jsonb)", account, sequence, json);
    }
    private static async Task ValidateUnique(NpgsqlConnection c, NpgsqlTransaction t, Guid account, PhraseEntity entity)
    {
        if (entity.Kind == "group")
        {
            if (await Database.Scalar(c, t, "SELECT id FROM quick_phrase_groups WHERE account_id=$1 AND NOT deleted AND id<>$2 AND lower(data->>'title')=lower($3)", account, entity.Id, entity.Title) is not null)
                throw new ApiException(409, "GROUP_NAME_CONFLICT", "已有同名分组，请修改名称。");
        }
        if (entity.Kind != "phrase") return;
        if (entity.Alias is not null && await Database.Scalar(c, t, "SELECT id FROM quick_phrases WHERE account_id=$1 AND NOT deleted AND id<>$2 AND data->>'alias'=$3", account, entity.Id, entity.Alias) is not null)
            throw new ApiException(409, "ALIAS_CONFLICT", "已有同名别名，请修改或清空。");
        if (entity.OriginPresetId is not null && await Database.Scalar(c, t, "SELECT data::text FROM quick_phrases WHERE account_id=$1 AND NOT deleted AND id<>$2 AND data->>'originPresetId'=$3", account, entity.Id, entity.OriginPresetId) is string source)
            throw new PresetConflict(Protocol.Decode<PhraseEntity>(source));
        if (entity.GroupId is not null && (await Read(c, t, account, "group", entity.GroupId)) is not { DeletedAt: null })
            throw new ApiException(409, "GROUP_DELETED", "分组已删除，请选择未分组或其他分组。");
    }
    public async Task<PhraseOperationsResponse> Apply(AuthSession session, PhraseOperationsRequest request)
    {
        if (request.Operations is null || request.Operations.Count is < 1 or > 100 || request.Operations.Any(op => op is null || op.Entity is null)) throw new ApiException(400, "INVALID_BATCH", "一次需提交 1–100 个有效操作。");
        await using var c = await database.Open(); await using var t = await c.BeginTransactionAsync();
        var state = await Database.LockAccount(c, t, session.AccountId);
        if (state.Epoch != request.SyncEpoch) throw new ApiException(409, "EPOCH_CHANGED", "账号数据空间已变化，请重新登录；原草稿已保留。");
        var sequence = (long)(await Database.Scalar(c, t, "SELECT phrase_sequence FROM accounts WHERE id=$1", session.AccountId))!;
        List<PhraseResult> results = [];
        foreach (var op in request.Operations)
        {
            if (!Guid.TryParse(op.OperationId, out var opId)) throw new ApiException(400, "INVALID_ID", "操作 ID 无效。");
            if (await Database.Scalar(c, t, "SELECT result::text FROM quick_phrase_operations WHERE account_id=$1 AND id=$2", session.AccountId, opId) is string cached) { results.Add(Protocol.Decode<PhraseResult>(cached)); continue; }
            var before = sequence; await t.SaveAsync("phrase_operation"); PhraseResult result;
            try
            {
                if (op.Type is not ("create" or "update" or "delete")) throw new InvalidDataException("操作类型无效。");
                var entity = op.Entity.Copy();
                // Identity is validated for delete too; body validation is unnecessary for tombstones.
                _ = Table(entity.Kind);
                if ((entity.Kind == "preference" && !PhraseEntity.PresetIdValid(entity.Id)) || (entity.Kind != "preference" && !Guid.TryParse(entity.Id, out _))) throw new InvalidDataException("实体 ID 无效。");
                var existing = await Read(c, t, session.AccountId, entity.Kind, entity.Id);
                if (existing?.DeletedAt is not null) result = new(op.OperationId, "deleted", existing, "PHRASE_DELETED");
                else if (op.Type == "delete")
                {
                    if (existing is null) result = new(op.OperationId, "rejected", null, "NOT_FOUND");
                    else
                    {
                        List<PhraseEntity> related = [];
                        if (existing.Kind == "group")
                        {
                            List<PhraseEntity> members = [];
                            await using (var command = Database.Command(c, t, "SELECT data::text FROM quick_phrases WHERE account_id=$1 AND NOT deleted AND data->>'groupId'=$2", session.AccountId, existing.Id))
                            await using (var reader = await command.ExecuteReaderAsync()) while (await reader.ReadAsync()) members.Add(Protocol.Decode<PhraseEntity>(reader.GetString(0)));
                            foreach (var member in members) { member.GroupId = null; await Write(c, t, session.AccountId, member, ++sequence); related.Add(member); }
                        }
                        existing.DeletedAt = Protocol.Now; existing.Body = ""; existing.Alias = null; existing.OriginPresetId = null; existing.OriginPresetVersion = null;
                        await Write(c, t, session.AccountId, existing, ++sequence); result = new(op.OperationId, "accepted", existing, Related: related);
                    }
                }
                else if ((op.Type == "create" && existing is not null) || (existing is not null && existing.Revision != op.ExpectedRevision)) result = new(op.OperationId, "conflict", existing, "REVISION_CONFLICT");
                else if (op.Type == "update" && existing is null && entity.Kind != "preference") result = new(op.OperationId, "rejected", null, "NOT_FOUND");
                else if (existing is null && op.ExpectedRevision != "0") result = new(op.OperationId, "conflict", null, "REVISION_CONFLICT");
                else
                {
                    List<PhraseEntity> related = [];
                    entity.Validate();
                    if (op.NewGroup is { } proposedGroup)
                    {
                        if (entity.Kind != "phrase" || proposedGroup.Kind != "group") throw new InvalidDataException("分组操作无效。");
                        proposedGroup = proposedGroup.Copy(); proposedGroup.Validate();
                        // Reuse a concurrently created category group, atomically with the phrase.
                        var same = await Database.Scalar(c, t, "SELECT data::text FROM quick_phrase_groups WHERE account_id=$1 AND NOT deleted AND lower(data->>'title')=lower($2)", session.AccountId, proposedGroup.Title);
                        if (same is string groupJson) entity.GroupId = Protocol.Decode<PhraseEntity>(groupJson).Id;
                        else
                        {
                            if (await Read(c, t, session.AccountId, "group", proposedGroup.Id) is not null) throw new InvalidDataException("分组 ID 已使用。");
                            await CheckCount(c, t, session.AccountId, "group"); proposedGroup.DeletedAt = null; proposedGroup.CreatedAt = Protocol.Now;
                            await Write(c, t, session.AccountId, proposedGroup, ++sequence); related.Add(proposedGroup); entity.GroupId = proposedGroup.Id;
                        }
                    }
                    await ValidateUnique(c, t, session.AccountId, entity);
                    if (existing is null) await CheckCount(c, t, session.AccountId, entity.Kind);
                    entity.DeletedAt = null; entity.CreatedAt = existing?.CreatedAt ?? Protocol.Now;
                    if (existing is not null && (entity.OriginPresetId != existing.OriginPresetId)) throw new InvalidDataException("已有短语的预置来源不可改变；请另存副本。");
                    var used = Convert.ToInt64(await Database.Scalar(c, t, "SELECT (SELECT coalesce(sum(byte_length),0) FROM uploads WHERE account_id=$1)+(SELECT coalesce(sum(octet_length(data::text)),0) FROM records WHERE account_id=$1 AND NOT deleted)+coalesce((SELECT used_bytes FROM quick_phrase_usage WHERE account_id=$1),0)", session.AccountId));
                    if (used + System.Text.Encoding.UTF8.GetByteCount(Protocol.Encode(entity)) - (existing is null ? 0 : System.Text.Encoding.UTF8.GetByteCount(Protocol.Encode(existing))) > Protocol.QuotaBytes) throw new ApiException(409, "QUOTA_EXCEEDED", "账号容量已满，内容保留在草稿中。");
                    await Write(c, t, session.AccountId, entity, ++sequence); result = new(op.OperationId, "accepted", entity, Related: related);
                }
            }
            catch (Exception error) when (error is InvalidDataException or ApiException or PresetConflict)
            {
                await t.RollbackAsync("phrase_operation"); sequence = before;
                result = error is PresetConflict conflict ? new(op.OperationId, "conflict", conflict.Existing, "PRESET_ALREADY_CUSTOMIZED") : new(op.OperationId, "rejected", null, error is ApiException api ? api.Code : "INVALID_PHRASE");
            }
            await Database.Execute(c, t, "INSERT INTO quick_phrase_operations(account_id,id,result) VALUES($1,$2,$3::jsonb)", session.AccountId, opId, Protocol.Encode(result)); results.Add(result);
        }
        await Database.Execute(c, t, "UPDATE accounts SET phrase_sequence=$2 WHERE id=$1", session.AccountId, sequence); await t.CommitAsync(); return new(results, sequence.ToString());
    }
    private static async Task CheckCount(NpgsqlConnection c, NpgsqlTransaction t, Guid account, string kind)
    {
        var count = (long)(await Database.Scalar(c, t, $"SELECT count(*) FROM {Table(kind)} WHERE account_id=$1 AND NOT deleted", account))!;
        var limit = kind switch { "phrase" => 2000, "group" => 100, _ => 4000 };
        if (count >= limit) throw new ApiException(409, "COUNT_EXCEEDED", "短语或分组数量已达到上限。");
    }
    public async Task<PhraseChanges> Changes(AuthSession session, long after, int limit)
    {
        if (after < 0 || limit is < 1 or > 200) throw new ApiException(400, "INVALID_CURSOR", "游标或分页大小无效。");
        await using var c = await database.Open(); await using var t = await c.BeginTransactionAsync(); var state = await Database.LockAccount(c, t, session.AccountId);
        var high = (long)(await Database.Scalar(c, t, "SELECT phrase_sequence FROM accounts WHERE id=$1", session.AccountId))!;
        var earliest = (long)(await Database.Scalar(c, t, "SELECT coalesce(min(sequence),0) FROM quick_phrase_changes WHERE account_id=$1", session.AccountId))!;
        if (after > high || (after < high && (earliest == 0 || after < earliest - 1))) throw new ApiException(409, "CURSOR_EXPIRED", "需要重新获取个人短语快照。");
        List<PhraseEntity> entities = [];
        await using (var command = Database.Command(c, t, "SELECT data::text FROM quick_phrase_changes WHERE account_id=$1 AND sequence>$2 AND sequence<=$3 ORDER BY sequence LIMIT $4", session.AccountId, after, high, limit))
        await using (var reader = await command.ExecuteReaderAsync()) while (await reader.ReadAsync()) entities.Add(Protocol.Decode<PhraseEntity>(reader.GetString(0)));
        var cursor = entities.Count == 0 ? after : entities[^1].Version; await t.CommitAsync(); return new(entities, cursor.ToString(), high.ToString(), cursor < high, state.Epoch);
    }
    public async Task<PhraseSnapshot> Snapshot(AuthSession session)
    {
        await using var c = await database.Open(); await using var t = await c.BeginTransactionAsync(); var state = await Database.LockAccount(c, t, session.AccountId);
        var cursor = (long)(await Database.Scalar(c, t, "SELECT phrase_sequence FROM accounts WHERE id=$1", session.AccountId))!;
        var json = (string)(await Database.Scalar(c, t, $"SELECT coalesce(jsonb_agg(data ORDER BY data->>'kind',data->>'id'),'[]'::jsonb)::text FROM ({All}) items", session.AccountId))!;
        var id = Guid.NewGuid(); await Database.Execute(c, t, "INSERT INTO quick_phrase_snapshots(id,account_id,cursor,epoch,entities) VALUES($1,$2,$3,$4,$5::jsonb)", id, session.AccountId, cursor, Guid.Parse(state.Epoch), json); await t.CommitAsync(); return new(id.ToString(), cursor.ToString(), state.Epoch);
    }
    public async Task<PhraseSnapshotPage> SnapshotPage(AuthSession session, Guid id, int page)
    {
        if (page < 0 || page > 100000) throw new ApiException(400, "INVALID_PAGE", "分页无效。");
        await using var c = await database.Open();
        await using var command = Database.Command(c, null, "SELECT cursor,epoch::text,entities::text FROM quick_phrase_snapshots WHERE id=$1 AND account_id=$2 AND expires_at>now()", id, session.AccountId);
        await using var reader = await command.ExecuteReaderAsync(); if (!await reader.ReadAsync()) throw new ApiException(404, "SNAPSHOT_EXPIRED", "快照已过期，请重试。");
        var entities = Protocol.Decode<List<PhraseEntity>>(reader.GetString(2)); var offset = (long)page * 200;
        return new(entities.Skip((int)offset).Take(200).ToList(), offset + 200 < entities.Count, reader.GetInt64(0).ToString(), reader.GetString(1));
    }
    private sealed class PresetConflict(PhraseEntity existing) : Exception { public PhraseEntity Existing { get; } = existing; }
}
