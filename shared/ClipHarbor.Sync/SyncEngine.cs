using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;

namespace ClipHarbor.Sync;

public sealed class SyncEngine : IAsyncDisposable
{
    public SyncApi Api { get; }
    public SyncConfig Config { get; }
    public SyncJournal Journal { get; }
    public string Space { get; }
    public Func<Task<long>> ClipboardVersion { get; init; } = () => Task.FromResult(0L);
    public Func<ReceivedRecord, bool, long, Task> Receive { get; init; } = (_, _, _) => Task.CompletedTask;
    public Func<WireRecord, string, string?, Task> Bind { get; init; } = (_, _, _) => Task.CompletedTask;
    public Func<bool> Unlocked { get; init; } = () => true;
    public event Action<string>? Status;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentDictionary<string, Task> _sending = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _materializing = new();
    private readonly ConcurrentDictionary<string, (long Version, long Connection, string EventId)> _liveCopies = new();
    private readonly SemaphoreSlim _latestGate = new(1, 1);
    private readonly SemaphoreSlim _historyGate = new(1, 1);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _operationGates = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _recordGates = new();
    private readonly ConcurrentDictionary<string, bool> _inflight = new();
    private readonly ConcurrentDictionary<string, (WireRecord Record, long Version, long Connection, string EventId)> _readyCopies = new();
    private readonly Dictionary<string, long> _applied = new();
    private (ClipboardLatest Latest, long Version, long Connection)? _candidate;
    private Task? _receiving;
    private Task? _run, _history;
    private volatile bool _live;
    private long _connection, _incomingSequence;
    private bool _epochBlocked;
    private readonly string _instanceId;
    private DateTimeOffset _lastCleanup = DateTimeOffset.MinValue;
    private long _lastProgress;
    public bool Connected => _live;
    public SyncEngine(SyncApi api, SyncConfig config, string root, string instanceId)
    {
        Api = api; Config = config; _instanceId = instanceId;
        var session = api.Session ?? throw new InvalidOperationException("需要登录。");
        Space = Protocol.Hash(Encoding.UTF8.GetBytes(api.BaseUri.AbsoluteUri + "|" + instanceId + "|" + session.AccountId));
        Journal = new(Path.Combine(root, "Sync", Space, session.SyncEpoch));
        if (Journal.GetState("epoch") == "") Journal.SetState("epoch", session.SyncEpoch);
        Api.Progress = (received, total) =>
        {
            var now = Environment.TickCount64;
            if (received != total && now - Interlocked.Read(ref _lastProgress) < 250) return;
            Interlocked.Exchange(ref _lastProgress, now);
            Status?.Invoke($"附件传输：{received / 1048576.0:F1} / {total / 1048576.0:F1} MB");
        };
    }
    public void Start() { if (_run is null) { if (!Config.Enabled) Status?.Invoke("自动同步已暂停"); _run = Run(); } }
    public async Task Capture(WireRecord wire, List<string> paths, long clipboardVersion, bool realCopy)
    {
        if (!Config.Accepts(wire.Kind) || _epochBlocked) return;
        var operationId = Guid.NewGuid().ToString();
        var sourceId = wire.RecordId;
        var connection = Interlocked.Read(ref _connection);
        var live = realCopy && Config.Enabled && _live;
        try
        {
            var attachments = await Task.Run(async () =>
            {
                List<LocalAttachment> files = [];
                if (wire.Kind is not ("image" or "files")) return files;
                if (paths.Count == 0 || paths.Count > 100) throw new InvalidDataException("文件数量无效。");
                var cache = Path.Combine(Journal.DirectoryPath, "Send", operationId); Directory.CreateDirectory(cache);
                long total = 0;
                foreach (var (source, index) in paths.Select((p, i) => (p, i)))
                {
                    var before = new FileInfo(source);
                    if (!before.Exists || before.LinkTarget is not null || before.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new InvalidDataException("文件缺失、不可读或不是普通文件。");
                    var limit = wire.Kind == "image" ? Protocol.MaxImageBytes : Protocol.MaxFileBytes;
                    if (before.Length > limit || (total += before.Length) > Protocol.MaxBatchBytes) throw new InvalidDataException("文件大小超过同步限制。");
                    var cacheBytes = Directory.EnumerateFiles(Journal.DirectoryPath, "*", SearchOption.AllDirectories).Sum(p => new FileInfo(p).Length);
                    if (cacheBytes + before.Length > Protocol.QuotaBytes) throw new InvalidDataException("本机同步缓存已满，请先清理历史。");
                    var length = before.Length; var modified = before.LastWriteTimeUtc; var target = Path.Combine(cache, index + ".bin");
                    await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true))
                    await using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true)) await input.CopyToAsync(output, _lifetime.Token);
                    before.Refresh();
                    if (!before.Exists || before.Length != length || before.LastWriteTimeUtc != modified || new FileInfo(target).Length != length) throw new InvalidDataException("文件在读取期间发生变化，请重新复制。");
                    await using var snapshot = File.OpenRead(target);
                    var hash = Convert.ToHexString(await SHA256.HashDataAsync(snapshot, _lifetime.Token)).ToLowerInvariant();
                    files.Add(new(target, wire.Kind == "image" ? "image" : "file", Path.GetFileName(source), length, hash));
                }
                return files;
            }, _lifetime.Token);
            if (wire.Kind == "image") { wire.BlobId = Guid.Empty.ToString(); wire.BlobSha256 = attachments[0].Sha256; }
            if (wire.Kind == "files") wire.Files = attachments.Select(f => new WireFile(f.Name, f.ByteLength, f.Sha256, Guid.Empty.ToString())).ToList();
            wire.ContentHash = wire.Digest();
            wire.Validate();
            var previous = Journal.Record(wire.RecordId);
            if (previous?.DeletedAt is not null || (previous is not null && previous.ContentHash != wire.ContentHash)) wire.RecordId = Guid.NewGuid().ToString();
            else wire.RecordId = Journal.Resolve(wire.RecordId);
            Journal.Enqueue(new(new(operationId, realCopy ? "capture" : "create", wire.RecordId, wire), attachments, sourceId));
            if (realCopy) Journal.SetState("clipboard-pin", wire.Kind == "files" ? wire.RecordId : "");
            if (live) _liveCopies[operationId] = (clipboardVersion, connection, Guid.NewGuid().ToString());
            await Bind(wire, Space, sourceId);
            Status?.Invoke(Config.Enabled ? "等待同步" : "自动同步已暂停，操作已保存在本机");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or FormatException)
        {
            var cache = Path.Combine(Journal.DirectoryPath, "Send", operationId); if (!Journal.Pending().Any(p => p.Operation.OperationId == operationId) && Directory.Exists(cache)) try { Directory.Delete(cache, true); } catch (IOException) { }
            Status?.Invoke("同步失败：" + error.Message);
        }
    }
    public void Edit(string recordId, string type, bool? favorite = null, string? note = null)
    {
        var record = Journal.Record(recordId);
        if (record is null || _epochBlocked) return;
        var expected = type == "favorite" ? record.FavoriteRevision : record.NoteRevision;
        var previous = Journal.Pending().LastOrDefault(p => Journal.Resolve(p.Operation.RecordId) == Journal.Resolve(recordId) && p.Operation.Type == type);
        if (previous is not null && !_sending.ContainsKey(previous.Operation.OperationId) && !_inflight.ContainsKey(previous.Operation.OperationId))
        {
            Journal.UpdatePending(previous with { Operation = previous.Operation with { Favorite = favorite, Note = note } }); return;
        }
        Journal.Enqueue(new(new(Guid.NewGuid().ToString(), type, Journal.Resolve(recordId), Favorite: favorite, Note: note, ExpectedRevision: record.Revision == 0 || previous is not null ? -1 : expected), []));
    }
    public void Delete(string id) { if (Journal.Record(id) is not null) Journal.Enqueue(new(new(Guid.NewGuid().ToString(), "delete", Journal.Resolve(id)), [])); }
    public void Hide(string id) => Journal.Hide(id);
    public async Task SyncNow()
    {
        if (_epochBlocked) { Status?.Invoke("服务器恢复了旧数据，请重新登录以建立新快照；旧队列已保留。"); return; }
        await VerifyInstance(_lifetime.Token);
        await PullHistory(_lifetime.Token);
        foreach (var pending in Journal.Pending()) if (Config.Accepts(pending.Operation.Record?.Kind ?? Journal.Record(pending.Operation.RecordId)?.Kind ?? "text")) await SendPending(pending, _lifetime.Token);
        await PullHistory(_lifetime.Token);
    }
    private async Task Run()
    {
        var token = _lifetime.Token;
        while (!token.IsCancellationRequested)
        {
            if (!Config.Enabled || _epochBlocked) { _live = false; await Task.Delay(500, token); continue; }
            try
            {
                Status?.Invoke("正在连接同步服务器");
                await VerifyInstance(token);
                using var socket = await Api.Connect(token);
                Interlocked.Increment(ref _connection); _liveCopies.Clear(); _readyCopies.Clear(); _candidate = null;
                var hello = await Notice(socket, token);
                if (hello.Type != "hello") throw new InvalidDataException("服务器未建立实时会话。");
                CheckEpoch(hello.SyncEpoch);
                Interlocked.Exchange(ref _incomingSequence, long.Parse(hello.ClipboardSequence)); _live = true;
                Status?.Invoke(Config.DirectPaste ? "已连接 · 可以跨设备直接粘贴" : "已连接 · 本机直接粘贴已关闭");
                using var connectionLife = CancellationTokenSource.CreateLinkedTokenSource(token);
                var tick = Tick(connectionLife.Token);
                try
                {
                    while (socket.State == WebSocketState.Open && Config.Enabled)
                    {
                        var notice = await Notice(socket, connectionLife.Token); CheckEpoch(notice.SyncEpoch);
                        if (notice.Type is "clipboard" or "heartbeat") _ = PollLatest(connectionLife.Token);
                    }
                }
                finally { _live = false; Interlocked.Increment(ref _connection); _liveCopies.Clear(); _readyCopies.Clear(); _candidate = null; connectionLife.Cancel(); try { await tick; if (_receiving is not null) await _receiving; } catch (OperationCanceledException) { } }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                _live = false; Status?.Invoke(error is SyncHttpException { Status: 401 } ? "登录已失效，请重新登录" : "等待连接：" + error.Message);
                await Task.Delay(TimeSpan.FromSeconds(3), token);
            }
        }
    }
    private void CheckEpoch(string epoch)
    {
        if (epoch != Journal.GetState("epoch")) { _epochBlocked = true; _live = false; throw new InvalidDataException("服务器数据空间已变化，请重新登录；原队列已保留。"); }
    }
    private async Task VerifyInstance(CancellationToken token)
    {
        if ((await Api.Test(token)).InstanceId != _instanceId)
        {
            _epochBlocked = true; _live = false;
            throw new InvalidDataException("服务器实例已变化，请重新登录；本机历史和旧队列已保留。");
        }
    }
    private static async Task<EventNotice> Notice(ClientWebSocket socket, CancellationToken token)
    {
        var buffer = new byte[4096]; var result = await socket.ReceiveAsync(buffer, token);
        if (result.MessageType != WebSocketMessageType.Text || !result.EndOfMessage) throw new WebSocketException("实时连接已结束。");
        return Protocol.Decode<EventNotice>(Encoding.UTF8.GetString(buffer, 0, result.Count));
    }
    private async Task Tick(CancellationToken token)
    {
        while (!token.IsCancellationRequested && Config.Enabled)
        {
            if (_history is null || _history.IsCompleted) _history = Quiet(() => PullHistory(token));
            var busyRecords = new HashSet<string>(_sending.Keys.Select(id => Journal.Pending().FirstOrDefault(p => p.Operation.OperationId == id)?.Operation.RecordId ?? ""));
            var queue = Journal.Pending();
            foreach (var pending in queue.OrderByDescending(p => _liveCopies.ContainsKey(p.Operation.OperationId)).ThenBy(p => p.Attachments.Count > 0))
            {
                if (_sending.Count >= 8) break;
                if (pending.Attachments.Count > 0 && _sending.Keys.Count(id => queue.FirstOrDefault(p => p.Operation.OperationId == id)?.Attachments.Count > 0) >= 2) continue;
                var record = pending.Operation.Record ?? Journal.Record(pending.Operation.RecordId);
                if (record is not null && !Config.Accepts(record.Kind) && pending.Operation.Type != "delete") continue;
                if (queue.TakeWhile(p => p.Operation.OperationId != pending.Operation.OperationId).Any(p => Journal.Resolve(p.Operation.RecordId) == Journal.Resolve(pending.Operation.RecordId))) continue;
                if (!busyRecords.Add(Journal.Resolve(pending.Operation.RecordId))) continue;
                var id = pending.Operation.OperationId;
                if (_sending.ContainsKey(id)) continue;
                var task = Quiet(() => SendPending(pending, token)); _sending[id] = task;
                _ = task.ContinueWith(completed => _sending.TryRemove(id, out _), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            foreach (var copy in _readyCopies.ToArray()) await Quiet(() => Publish(copy.Key, copy.Value, token));
            if (DateTimeOffset.UtcNow - _lastCleanup > TimeSpan.FromMinutes(1)) { CleanupCache(); _lastCleanup = DateTimeOffset.UtcNow; }
            await PollLatest(token);
            await Task.Delay(1000, token);
        }
    }
    private async Task Quiet(Func<Task> operation)
    {
        try { await operation(); }
        catch (OperationCanceledException) { }
        catch (Exception error) { Status?.Invoke("同步待重试：" + error.Message); }
    }
    private async Task SendPending(PendingOperation pending, CancellationToken token)
    {
        var gate = _operationGates.GetOrAdd(pending.Operation.OperationId, _ => new(1, 1));
        var operationId = pending.Operation.OperationId;
        await gate.WaitAsync(token);
        var recordGate = _recordGates.GetOrAdd(Journal.Resolve(pending.Operation.RecordId), _ => new(1, 1));
        try { await recordGate.WaitAsync(token); } catch { gate.Release(); throw; }
        _inflight[pending.Operation.OperationId] = true;
        try
        {
        pending = Journal.Pending().FirstOrDefault(p => p.Operation.OperationId == pending.Operation.OperationId)!;
        if (pending is null) return;
        var operation = pending.Operation;
        var id = Journal.Resolve(operation.RecordId); operation = operation with { RecordId = id };
        if (operation.Record is { } wire)
        {
            wire.RecordId = id;
            var uploaded = new List<WireFile>();
            foreach (var attachment in pending.Attachments)
            {
                var blob = await Api.Upload(attachment.Path, attachment.Kind, attachment.Sha256, token);
                if (wire.Kind == "image") wire.BlobId = blob;
                else uploaded.Add(new(attachment.Name, attachment.ByteLength, attachment.Sha256, blob));
            }
            if (wire.Kind == "files") wire.Files = uploaded;
            pending = pending with { Operation = operation }; Journal.UpdatePending(pending);
        }
        if (operation.ExpectedRevision == -1 && Journal.Record(id) is { } current) operation = operation with { ExpectedRevision = operation.Type == "favorite" ? current.FavoriteRevision : current.NoteRevision };
        var result = (await Api.Post<OperationsResponse>("sync/operations", new OperationsRequest(Journal.GetState("epoch"), [operation]), token)).Results.Single();
        Journal.Acknowledge(pending, result);
        if (result.Status == "accepted" && result.Record is { DeletedAt: null } record)
        {
            var projection = Protocol.Decode<WireRecord>(Protocol.Encode(record));
            foreach (var edit in Journal.Pending().Where(p => Journal.Resolve(p.Operation.RecordId) == record.RecordId))
            {
                if (edit.Operation.Type == "favorite" && edit.Operation.Favorite is bool favorite) projection.Favorite = favorite;
                if (edit.Operation.Type == "note" && edit.Operation.Note is string note) projection.Note = note;
            }
            await Bind(projection, Space, pending.SourceId);
            if (Journal.GetState("clipboard-pin") == pending.Operation.RecordId) Journal.SetState("clipboard-pin", record.RecordId);
            if (_liveCopies.TryRemove(operation.OperationId, out var copy))
                _readyCopies[operation.OperationId] = (record, copy.Version, copy.Connection, copy.EventId);
        }
        else if (result.Status != "accepted") Status?.Invoke(result.Status == "conflict" ? "存在备注／收藏冲突，草稿已保留，可在同步设置中查看" : "内容未同步：" + result.Code);
        foreach (var attachment in pending.Attachments) { try { File.Delete(attachment.Path); } catch (IOException) { } }
        }
        finally { _inflight.TryRemove(operationId, out _); recordGate.Release(); gate.Release(); }
    }
    private async Task Publish(string id, (WireRecord Record, long Version, long Connection, string EventId) copy, CancellationToken token)
    {
        if (!_live || !Config.Enabled || copy.Connection != Interlocked.Read(ref _connection) || await ClipboardVersion() != copy.Version) { _readyCopies.TryRemove(id, out _); return; }
        await Api.Post<ClipboardLatest>("clipboard/events", new ClipboardPublish(copy.EventId, copy.Record.RecordId, Journal.GetState("epoch")), token);
        _readyCopies.TryRemove(id, out _);
        Status?.Invoke("已发送到服务器 · 等待在线设备接收");
    }
    private async Task PullHistory(CancellationToken token)
    {
        await _historyGate.WaitAsync(token);
        try
        {
        var cursor = Journal.GetState("cursor", "0");
        try
        {
            ChangesResponse page;
            do
            {
                page = await Api.Get<ChangesResponse>("sync/changes?cursor=" + Uri.EscapeDataString(cursor), token); CheckEpoch(page.SyncEpoch);
                Journal.ApplyPage(page.Records, page.Cursor, page.SyncEpoch); cursor = page.Cursor;
            } while (page.HasMore);
        }
        catch (SyncHttpException error) when (error.Code == "CURSOR_EXPIRED")
        {
            string? pageToken = null; SnapshotResponse page;
            do
            {
                page = await Api.Get<SnapshotResponse>("sync/snapshot" + (pageToken is null ? "" : "?pageToken=" + Uri.EscapeDataString(pageToken)), token); CheckEpoch(page.SyncEpoch);
                Journal.ApplyPage(page.Records, cursor, page.SyncEpoch);
                pageToken = page.NextPageToken;
            } while (pageToken is not null);
            Journal.SetState("cursor", page.Cursor);
        }
        foreach (var record in Journal.Records(includeHidden: true).OrderByDescending(r => r.LastCapturedAt))
        {
            if (record.Revision == 0 || (_applied.TryGetValue(record.RecordId, out var revision) && revision >= record.Revision)) continue;
            if (record.DeletedAt is null && (!Config.Accepts(record.Kind) || Journal.Hidden(record.RecordId))) continue;
            try
            {
                var received = await Materialize(record, token);
                foreach (var pending in Journal.Pending().Where(p => Journal.Resolve(p.Operation.RecordId) == record.RecordId))
                {
                    if (pending.Operation.Type == "favorite" && pending.Operation.Favorite is bool favorite) received.Record.Favorite = favorite;
                    if (pending.Operation.Type == "note" && pending.Operation.Note is string note) received.Record.Note = note;
                }
                await Receive(received, false, 0); _applied[record.RecordId] = record.Revision;
            }
            catch (Exception error) when (error is not OperationCanceledException) { Status?.Invoke("历史接收待重试：" + error.Message); }
        }
        }
        finally { _historyGate.Release(); }
    }
    private async Task PollLatest(CancellationToken token)
    {
        if (!_live || !_latestGate.Wait(0)) return;
        try
        {
            var latest = await Api.Get<ClipboardLatest>("clipboard/latest", token); CheckEpoch(latest.SyncEpoch);
            var sequence = long.Parse(latest.ClipboardSequence);
            if (sequence > Interlocked.Read(ref _incomingSequence))
            {
                Interlocked.Exchange(ref _incomingSequence, sequence); _candidate = null;
                if (latest.Event is not null && latest.Record is not null && latest.Event.OriginDeviceId != Api.Session!.DeviceId && Config.DirectPaste && Config.Accepts(latest.Record.Kind) && Unlocked())
                    _candidate = (latest, await ClipboardVersion(), Interlocked.Read(ref _connection));
            }
            if (_candidate is not { } candidate || _receiving is { IsCompleted: false }) return;
            if (!Config.DirectPaste || !Unlocked() || candidate.Version != await ClipboardVersion()) { _candidate = null; return; }
            _receiving = Quiet(async () =>
            {
                var received = await Materialize(candidate.Latest.Record!, token);
                if (_live && candidate.Connection == Interlocked.Read(ref _connection) && Config.Enabled && Config.DirectPaste && Unlocked() && candidate.Latest.ClipboardSequence == Interlocked.Read(ref _incomingSequence).ToString() && candidate.Version == await ClipboardVersion())
                {
                    if (received.Record.Kind == "files") Journal.SetState("clipboard-pin", received.Record.RecordId);
                    await Receive(received, true, candidate.Version);
                    if (_candidate?.Latest.ClipboardSequence == candidate.Latest.ClipboardSequence) _candidate = null;
                    Status?.Invoke("已可粘贴 · 来自另一台设备");
                }
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { Status?.Invoke("实时接收待重试：" + error.Message); }
        finally { _latestGate.Release(); }
    }
    private async Task<ReceivedRecord> Materialize(WireRecord record, CancellationToken token)
    {
        if (record.DeletedAt is not null) return new(record, null, []);
        record.Validate();
        var gate = _materializing.GetOrAdd(record.RecordId, _ => new(1, 1)); await gate.WaitAsync(token);
        try
        {
            string? image = null; List<string> paths = [];
            var root = Path.Combine(Journal.DirectoryPath, "Received", record.RecordId); Directory.CreateDirectory(root);
            if (record.Kind == "image") { image = Path.Combine(root, "image.png"); if (!File.Exists(image) && CacheBytes() + Protocol.MaxImageBytes > Protocol.QuotaBytes) throw new InvalidDataException("本机同步缓存已满。"); await Api.Download(record.BlobId!, record.BlobSha256!, image, Protocol.MaxImageBytes, token); }
            if (record.Kind == "files")
            {
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var (file, index) in record.Files.Select((file, index) => (file, index)))
                {
                    var path = Path.Combine(root, index.ToString("D3"), Protocol.SafeName(file.Name, names));
                    var used = Directory.EnumerateFiles(Journal.DirectoryPath, "*", SearchOption.AllDirectories).Sum(p => new FileInfo(p).Length);
                    if (!File.Exists(path) && used + file.ByteLength > Protocol.QuotaBytes) throw new InvalidDataException("本机同步缓存已满。");
                    await Api.Download(file.BlobId, file.Sha256, path, file.ByteLength, token); paths.Add(path);
                }
            }
            return new(record, image, paths);
        }
        finally { gate.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        _live = false; _lifetime.Cancel();
        if (_run is not null) try { await _run; } catch (OperationCanceledException) { }
        if (_history is not null) try { await _history; } catch (OperationCanceledException) { }
        if (_receiving is not null) try { await _receiving; } catch (OperationCanceledException) { }
        try { await Task.WhenAll(_sending.Values); } catch (OperationCanceledException) { }
        Api.Dispose(); _lifetime.Dispose();
    }
    private long CacheBytes() => Directory.EnumerateFiles(Journal.DirectoryPath, "*", SearchOption.AllDirectories).Sum(p => new FileInfo(p).Length);
    private void CleanupCache()
    {
        var pin = Journal.Resolve(Journal.GetState("clipboard-pin"));
        foreach (var category in new[] { "Received", "Send" })
        {
            var root = Path.Combine(Journal.DirectoryPath, category); if (!Directory.Exists(root)) continue;
            var pending = Journal.Pending().Select(p => p.Operation.OperationId).ToHashSet();
            foreach (var directory in Directory.EnumerateDirectories(root))
            {
                var id = Path.GetFileName(directory);
                if (!Guid.TryParse(id, out _) || new DirectoryInfo(directory).LinkTarget is not null || DateTime.UtcNow - Directory.GetLastWriteTimeUtc(directory) < TimeSpan.FromHours(24)) continue;
                if (category == "Send" && pending.Contains(id)) continue;
                if (category == "Received" && (id == pin || Journal.Record(id) is { DeletedAt: null } && !Journal.Hidden(id))) continue;
                var gate = _materializing.GetOrAdd(id, _ => new(1, 1)); if (!gate.Wait(0)) continue;
                try { Directory.Delete(directory, true); } catch (IOException) { } finally { gate.Release(); }
            }
        }
    }
}
