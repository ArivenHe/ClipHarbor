using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using ClipHarbor.Sync;

namespace ClipHarbor.Server;

public sealed class Attachments(Database database, string directory)
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();
    private string Root(Guid account, Guid id) => Path.Combine(directory, "attachments", account.ToString(), id.ToString());
    public async Task<UploadResponse> Create(AuthSession session, UploadRequest request)
    {
        if (request.Kind is not ("file" or "image") || request.ByteLength < 0 || request.ByteLength > (request.Kind == "image" ? Protocol.MaxImageBytes : Protocol.MaxFileBytes) || !Protocol.IsHash(request.Sha256)) throw new ApiException(413, "ATTACHMENT_LIMIT", "附件格式或大小无效。");
        await using var connection = await database.Open();
        await using var transaction = await connection.BeginTransactionAsync();
        _ = await Database.LockAccount(connection, transaction, session.AccountId);
        Guid? existing = null;
        await using (var command = Database.Command(connection, transaction, "SELECT id,byte_length FROM uploads WHERE account_id=$1 AND sha256=$2 AND kind=$3", session.AccountId, request.Sha256, request.Kind))
        await using (var reader = await command.ExecuteReaderAsync())
            if (await reader.ReadAsync()) { if (reader.GetInt64(1) != request.ByteLength) throw new ApiException(409, "HASH_SIZE_MISMATCH", "附件大小与哈希不匹配。"); existing = reader.GetGuid(0); }
        if (existing is null)
        {
            var used = Convert.ToInt64(await Database.Scalar(connection, transaction, "SELECT coalesce(sum(byte_length),0)::bigint FROM uploads WHERE account_id=$1", session.AccountId));
            var textBytes = Convert.ToInt64(await Database.Scalar(connection, transaction, "SELECT coalesce(sum(octet_length(data::text)),0)::bigint FROM records WHERE account_id=$1 AND NOT deleted", session.AccountId));
            var phraseBytes = Convert.ToInt64(await Database.Scalar(connection, transaction, "SELECT coalesce(used_bytes,0) FROM quick_phrase_usage WHERE account_id=$1", session.AccountId) ?? 0L);
            if (used + textBytes + phraseBytes + request.ByteLength > Protocol.QuotaBytes) throw new ApiException(409, "QUOTA_EXCEEDED", "账号存储容量已满。");
            existing = Guid.NewGuid();
            await Database.Execute(connection, transaction, "INSERT INTO uploads(id,account_id,kind,byte_length,sha256) VALUES($1,$2,$3,$4,$5)", existing, session.AccountId, request.Kind, request.ByteLength, request.Sha256);
        }
        await transaction.CommitAsync();
        return await Status(session, existing.Value);
    }
    public async Task<(long Size, string Hash, string Kind, bool Completed)> Get(AuthSession session, Guid id)
    {
        await using var connection = await database.Open();
        await using var command = Database.Command(connection, null, "SELECT byte_length,sha256,kind,completed FROM uploads WHERE id=$1 AND account_id=$2", id, session.AccountId);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) throw new ApiException(404, "UPLOAD_NOT_FOUND", "上传会话不存在。");
        return (reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetBoolean(3));
    }
    public async Task<UploadResponse> Status(AuthSession session, Guid id)
    {
        var upload = await Get(session, id);
        var root = Root(session.AccountId, id);
        List<int> parts = [];
        if (Directory.Exists(root))
            foreach (var path in Directory.EnumerateFiles(root, "*.part")) if (int.TryParse(Path.GetFileNameWithoutExtension(path), out var index) && index >= 0) parts.Add(index);
        return new(id.ToString(), Protocol.PartBytes, parts.Order().ToList(), upload.Completed ? id.ToString() : null);
    }
    public async Task Put(AuthSession session, Guid id, int index, Stream body, CancellationToken token)
    {
        var gate = _locks.GetOrAdd(id, _ => new(1, 1));
        await gate.WaitAsync(token);
        try
        {
            var upload = await Get(session, id);
            var count = (upload.Size + Protocol.PartBytes - 1) / Protocol.PartBytes;
            if (index < 0 || index >= count) throw new ApiException(400, "INVALID_PART", "分块索引无效。");
            if (upload.Completed) return;
            var expected = Math.Min(Protocol.PartBytes, upload.Size - (long)index * Protocol.PartBytes);
            var root = Root(session.AccountId, id); Directory.CreateDirectory(root);
            var target = Path.Combine(root, index + ".part");
            var temporary = Path.Combine(root, Guid.NewGuid() + ".tmp");
            try
            {
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                {
                    var buffer = new byte[65536]; long length = 0; int read;
                    while ((read = await body.ReadAsync(buffer, token)) != 0)
                    {
                        length += read;
                        if (length > expected) throw new ApiException(413, "PART_SIZE", "分块超过大小限制。");
                        await output.WriteAsync(buffer.AsMemory(0, read), token);
                    }
                    if (length != expected) throw new ApiException(400, "PART_SIZE", "分块不完整。");
                }
                if (File.Exists(target))
                {
                    await using var left = File.OpenRead(target); await using var right = File.OpenRead(temporary);
                    var leftHash = await SHA256.HashDataAsync(left, token); var rightHash = await SHA256.HashDataAsync(right, token);
                    if (!CryptographicOperations.FixedTimeEquals(leftHash, rightHash)) throw new ApiException(409, "PART_CONFLICT", "同一分块内容不同，请重新建立上传。");
                }
                else File.Move(temporary, target);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        finally { gate.Release(); }
    }
    public async Task<UploadResponse> Complete(AuthSession session, Guid id, CancellationToken token)
    {
        var gate = _locks.GetOrAdd(id, _ => new(1, 1)); await gate.WaitAsync(token);
        try
        {
            var upload = await Get(session, id);
            if (upload.Completed) return await Status(session, id);
            var root = Root(session.AccountId, id); Directory.CreateDirectory(root);
            var temporary = Path.Combine(root, "complete.tmp"); var target = Path.Combine(root, "content.blob");
            try
            {
                await using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true))
                    for (var index = 0; (long)index * Protocol.PartBytes < upload.Size; index++)
                    {
                        var part = Path.Combine(root, index + ".part");
                        if (!File.Exists(part)) throw new ApiException(409, "PART_MISSING", "还有分块未上传。");
                        await using var input = File.OpenRead(part); await input.CopyToAsync(output, token);
                    }
                await using (var input = File.OpenRead(temporary))
                {
                    if (input.Length != upload.Size || Convert.ToHexString(await SHA256.HashDataAsync(input, token)).ToLowerInvariant() != upload.Hash) throw new ApiException(400, "HASH_MISMATCH", "附件完整性校验失败。");
                    if (upload.Kind == "image")
                    {
                        input.Position = 0; var header = new byte[24];
                        if (await input.ReadAsync(header, token) != 24 || !header.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) || !header.AsSpan(12, 4).SequenceEqual("IHDR"u8)) throw new ApiException(400, "IMAGE_INVALID", "需要有效 PNG 图片。");
                        var width = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(16)); var height = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(20));
                        if (width == 0 || height == 0 || (ulong)width * height > 40_000_000) throw new ApiException(413, "IMAGE_DIMENSIONS", "图片尺寸超过限制。");
                    }
                }
                File.Move(temporary, target, true);
                await using var connection = await database.Open();
                await Database.Execute(connection, null, "UPDATE uploads SET completed=true WHERE id=$1 AND account_id=$2", id, session.AccountId);
                foreach (var part in Directory.EnumerateFiles(root, "*.part")) File.Delete(part);
                return await Status(session, id);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        finally { gate.Release(); }
    }
    public async Task<(string Path, string Hash)> Download(AuthSession session, Guid id)
    {
        var upload = await Get(session, id);
        if (!upload.Completed) throw new ApiException(404, "ATTACHMENT_NOT_READY", "附件尚未完成。");
        await using var connection = await database.Open();
        var active = (bool?)await Database.Scalar(connection, null, "SELECT EXISTS(SELECT 1 FROM records WHERE account_id=$1 AND NOT deleted AND (data->>'blobId'=$2 OR data->'files' @> jsonb_build_array(jsonb_build_object('blobId',$2))))", session.AccountId, id.ToString());
        if (active != true) throw new ApiException(404, "ATTACHMENT_UNAVAILABLE", "附件没有活动记录引用。");
        var path = Path.Combine(Root(session.AccountId, id), "content.blob");
        if (!File.Exists(path)) throw new ApiException(404, "ATTACHMENT_MISSING", "附件文件缺失。");
        return (path, upload.Hash);
    }
    public async Task Cancel(AuthSession session, Guid id)
    {
        var gate = _locks.GetOrAdd(id, _ => new(1, 1)); await gate.WaitAsync();
        try
        {
            await using var connection = await database.Open();
            var deleted = await Database.Execute(connection, null, "DELETE FROM uploads WHERE id=$1 AND account_id=$2 AND NOT completed", id, session.AccountId);
            if (deleted != 0 && Directory.Exists(Root(session.AccountId, id))) Directory.Delete(Root(session.AccountId, id), true);
        }
        finally { gate.Release(); }
    }
}
