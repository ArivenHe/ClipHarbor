using ClipHarbor.Sync;
using System.Net;
using System.Net.WebSockets;
using System.Text;

var root = Path.Combine(Path.GetTempPath(), "clipharbor-integration-" + Guid.NewGuid()); Directory.CreateDirectory(root);
int assertions = 0;
void Check(bool value, string detail) { if (!value) throw new Exception(detail); assertions++; }
async Task Expect(Func<Task> operation, int status)
{
    try { await operation(); throw new Exception("Expected HTTP " + status); }
    catch (SyncHttpException error) { Check(error.Status == status, $"Expected {status}, got {error.Status}: {error.Message}"); }
}
async Task Wait(Func<bool> condition, string detail)
{
    var end = DateTime.UtcNow.AddSeconds(40);
    while (!condition() && DateTime.UtcNow < end) await Task.Delay(100);
    Check(condition(), detail);
}
WireRecord Text(string text) { var record = new WireRecord { Text = text }; record.ContentHash = record.Digest(); return record; }
try
{
    // Protocol and durable journal invariants, without the UI or network.
    var normalized = Text("中文\r\nline"); Check(normalized.ContentHash == Text("中文\nline").ContentHash, "newline framing");
    Check(Text("").ContentHash != new WireRecord().Digest(), "null and empty remain distinct");
    var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    Check(Protocol.SafeName("CON.txt", names) == "_CON.txt", "Windows reserved name");
    Check(Protocol.SafeName("A.txt", names) == "A.txt" && Protocol.SafeName("a.txt", names) == "a (2).txt", "case collisions");
    try { Protocol.SafeName("../bad", names); throw new Exception("path traversal accepted"); } catch (InvalidDataException) { assertions++; }
    try { Protocol.Server("http://example.com"); throw new Exception("remote HTTP accepted"); } catch (InvalidDataException) { assertions++; }
    Check(Protocol.Server("https://example.com/clipboard").AbsolutePath == "/clipboard/", "subpath preserved");
    var journal = new SyncJournal(Path.Combine(root, "journal")); var local = Text("journal"); var pending = new PendingOperation(new(Guid.NewGuid().ToString(), "create", local.RecordId, local), []);
    journal.Enqueue(pending); journal.Hide(local.RecordId);
    var canonical = Protocol.Decode<WireRecord>(Protocol.Encode(local)); canonical.RecordId = Guid.NewGuid().ToString(); canonical.Revision = 7;
    journal.Acknowledge(pending, new(pending.Operation.OperationId, "accepted", canonical));
    var reopened = new SyncJournal(journal.DirectoryPath);
    Check(reopened.Pending().Count == 0 && reopened.Resolve(local.RecordId) == canonical.RecordId && reopened.Hidden(canonical.RecordId), "atomic alias migration and hiding");
    canonical.Revision = 6; reopened.ApplyPage([canonical], "6", "epoch"); Check(reopened.Record(canonical.RecordId)!.Revision == 7, "older revision ignored");
    var conflict = new PendingOperation(new(Guid.NewGuid().ToString(), "note", canonical.RecordId, Note: "draft", ExpectedRevision: 6), []);
    reopened.Enqueue(conflict); reopened.Acknowledge(conflict, new(conflict.Operation.OperationId, "conflict", canonical, "FIELD_CONFLICT"));
    Check(File.ReadAllText(reopened.ExportFailures()).Contains("draft"), "conflict draft preserved");
    Console.WriteLine("PASS protocol and durable journal");

    var url = Protocol.Server(Environment.GetEnvironmentVariable("CLIPHARBOR_TEST_URL") ?? "http://localhost:28080", true);
    var password = Environment.GetEnvironmentVariable("CLIPHARBOR_TEST_PASSWORD") ?? "integration-test-only";
    using var a = new SyncApi(url); using var b = new SyncApi(url); using var outsider = new SyncApi(url);
    await a.Login(new("alice", password, "Mac test", Guid.NewGuid().ToString()));
    await b.Login(new("alice", password, "Windows test", Guid.NewGuid().ToString()));
    await outsider.Login(new("bob", password, "isolated account", Guid.NewGuid().ToString()));
    async Task<OperationResult> Apply(SyncApi api, SyncOperation operation) => (await api.Post<OperationsResponse>("sync/operations", new OperationsRequest(api.Session!.SyncEpoch, [operation]))).Results.Single();
    var record = Text("cross platform " + Guid.NewGuid()); var op = new SyncOperation(Guid.NewGuid().ToString(), "create", record.RecordId, record);
    var created = (await Apply(a, op)).Record!;
    Check(created.Revision > 0, "server revision assigned"); Check((await Apply(a, op)).Record!.Revision == created.Revision, "idempotent replay");
    var duplicate = Text(record.Text!); var merged = (await Apply(b, new(Guid.NewGuid().ToString(), "capture", duplicate.RecordId, duplicate))).Record!;
    Check(merged.RecordId == created.RecordId, "canonical dedup across devices");
    var favorite = (await Apply(a, new(Guid.NewGuid().ToString(), "favorite", created.RecordId, Favorite: true, ExpectedRevision: created.FavoriteRevision))).Record!;
    var note = await Apply(b, new(Guid.NewGuid().ToString(), "note", created.RecordId, Note: "one", ExpectedRevision: created.NoteRevision));
    Check(note.Status == "accepted" && note.Record!.Favorite, "per-field concurrency");
    Check((await Apply(a, new(Guid.NewGuid().ToString(), "note", created.RecordId, Note: "stale", ExpectedRevision: created.NoteRevision))).Status == "conflict", "same-field conflict");
    Check((await outsider.Get<ChangesResponse>("sync/changes?cursor=0")).Records.Count == 0, "account history isolated");
    await Expect(() => outsider.Post<ClipboardLatest>("clipboard/events", new ClipboardPublish(Guid.NewGuid().ToString(), created.RecordId, outsider.Session!.SyncEpoch)), 404);
    var eventId = Guid.NewGuid().ToString(); var latest = await a.Post<ClipboardLatest>("clipboard/events", new ClipboardPublish(eventId, created.RecordId, a.Session!.SyncEpoch));
    Check((await a.Post<ClipboardLatest>("clipboard/events", new ClipboardPublish(eventId, created.RecordId, a.Session.SyncEpoch))).ClipboardSequence == latest.ClipboardSequence, "clipboard event idempotency");
    Check((await b.Get<ClipboardLatest>("clipboard/latest")).Event!.OriginDeviceId == a.Session.DeviceId, "clipboard origin identity");

    var bytes = new byte[Protocol.PartBytes + 31]; new Random(7).NextBytes(bytes); var hash = Protocol.Hash(bytes); var upload = await a.Post<UploadResponse>("uploads", new UploadRequest("file", bytes.Length, hash));
    await Expect(() => outsider.Get<UploadResponse>("uploads/" + upload.UploadId), 404);
    async Task Part(int index, byte[] data) { using var result = await a.Send(() => new(HttpMethod.Put, $"api/v1/uploads/{upload.UploadId}/parts/{index}") { Content = new ByteArrayContent(data) }, true, default); }
    await Part(0, bytes[..Protocol.PartBytes]); Check((await a.Get<UploadResponse>("uploads/" + upload.UploadId)).ReceivedParts.SequenceEqual([0]), "resumable parts");
    await Part(0, bytes[..Protocol.PartBytes]); var tampered = bytes[..Protocol.PartBytes]; tampered[0] ^= 1; await Expect(() => Part(0, tampered), 409);
    await Expect(() => a.Post<UploadResponse>($"uploads/{upload.UploadId}/complete", new { }), 409);
    await Part(1, bytes[Protocol.PartBytes..]); var blob = (await a.Post<UploadResponse>($"uploads/{upload.UploadId}/complete", new { })).BlobId!;
    await Expect(() => a.Download(blob, hash, Path.Combine(root, "unlinked"), bytes.Length, default), 404);
    var files = new WireRecord { Kind = "files", Files = [new("CON.txt", bytes.Length, hash, blob), new("con.TXT", bytes.Length, hash, blob)] }; files.ContentHash = files.Digest();
    Check((await Apply(a, new(Guid.NewGuid().ToString(), "create", files.RecordId, files))).Status == "accepted", "file manifest committed");
    var target = Path.Combine(root, "download"); await File.WriteAllBytesAsync(target + ".part", bytes[..777]); await b.Download(blob, hash, target, bytes.Length, default);
    Check((await File.ReadAllBytesAsync(target)).SequenceEqual(bytes), "range download verified");
    await File.WriteAllBytesAsync(target + ".part", bytes); File.Delete(target); await b.Download(blob, hash, target, bytes.Length, default); Check(File.Exists(target), "complete partial file recovered");
    await Expect(() => outsider.Download(blob, hash, Path.Combine(root, "outsider"), bytes.Length, default), 404);
    var zeroPath = Path.Combine(root, "zero"); await File.WriteAllBytesAsync(zeroPath, []); var zeroHash = Protocol.Hash([]); var zeroBlob = await a.Upload(zeroPath, "file", zeroHash, default);
    var empty = new WireRecord { Kind = "files", Files = [new("empty.txt", 0, zeroHash, zeroBlob)] }; empty.ContentHash = empty.Digest(); await Apply(a, new(Guid.NewGuid().ToString(), "create", empty.RecordId, empty));
    await b.Download(zeroBlob, zeroHash, Path.Combine(root, "zero-copy"), 0, default); Check(new FileInfo(Path.Combine(root, "zero-copy")).Length == 0, "zero-byte file");
    var invalid = new WireRecord { Kind = "files", Files = [new("bad.txt", 0, zeroHash, Guid.NewGuid().ToString())] }; invalid.ContentHash = invalid.Digest();
    Check((await Apply(a, new(Guid.NewGuid().ToString(), "create", invalid.RecordId, invalid))).Status == "rejected", "missing attachment rejected");
    Console.WriteLine("PASS API, metadata conflicts, account isolation, resumable files");

    // Two real engines exercise WebSocket delivery, file hydration, reconnect and clipboard races.
    var config = new SyncConfig { Enabled = true, DirectPaste = true }; long av = 10, bv = 20; int directCount = 0; string? directText = null; List<string>? directFiles = null; bool locked = false; int failHistory = 1; int historySuccess = 0;
    using var ca = new SyncApi(url, a.Session); using var cb = new SyncApi(url, b.Session);
    var instance = (await a.Test()).InstanceId;
    await using var ea = new SyncEngine(ca, config, Path.Combine(root, "a"), instance) { ClipboardVersion = () => Task.FromResult(Interlocked.Read(ref av)) };
    await using var eb = new SyncEngine(cb, new SyncConfig { Enabled = true }, Path.Combine(root, "b"), instance)
    {
        ClipboardVersion = () => Task.FromResult(Interlocked.Read(ref bv)), Unlocked = () => !locked,
        Receive = (received, direct, expected) =>
        {
            if (direct) { Check(expected == Interlocked.Read(ref bv), "native anchor still current"); directText = received.Record.Text; directFiles = received.FilePaths; Interlocked.Increment(ref bv); Interlocked.Increment(ref directCount); }
            else if (received.Record.RecordId == files.RecordId) { if (Interlocked.Exchange(ref failHistory, 0) == 1) throw new IOException("injected native persistence failure"); Interlocked.Increment(ref historySuccess); }
            return Task.CompletedTask;
        }
    };
    ea.Start(); eb.Start(); await Wait(() => ea.Connected && eb.Connected, "engines connected");
    await Wait(() => historySuccess > 0, "history materialization retried after cursor committed"); Check(directCount == 0, "initial history never overwrites clipboard");
    var live = Text("direct text " + Guid.NewGuid()); await ea.Capture(live, [], Interlocked.Increment(ref av), true);
    await Wait(() => directText == live.Text, "copy on A becomes native clipboard on B");
    var seq = (await a.Get<ClipboardLatest>("clipboard/latest")).ClipboardSequence;
    var historyOnly = Text("history only " + Guid.NewGuid()); await ea.Capture(historyOnly, [], Interlocked.Read(ref av), false); await ea.SyncNow();
    Check((await a.Get<ClipboardLatest>("clipboard/latest")).ClipboardSequence == seq, "history-only capture not promoted");
    var source = Path.Combine(root, "source-file.txt"); await File.WriteAllBytesAsync(source, bytes);
    var fileCopy = new WireRecord { Kind = "files" }; await ea.Capture(fileCopy, [source], Interlocked.Increment(ref av), true);
    await Wait(() => directFiles?.Count == 1, "file copy delivered to native adapter"); Check((await File.ReadAllBytesAsync(directFiles![0])).SequenceEqual(bytes), "native file references point to verified bytes");
    var countBefore = directCount; locked = true; await ea.Capture(Text("locked " + Guid.NewGuid()), [], Interlocked.Increment(ref av), true); await ea.SyncNow(); await Task.Delay(2500); locked = false; await Task.Delay(1500); Check(directCount == countBefore, "locked clipboard event skipped, not replayed after unlocking");
    config.Enabled = false; await Task.Delay(1500); await ea.Capture(Text("offline " + Guid.NewGuid()), [], Interlocked.Increment(ref av), true); seq = (await a.Get<ClipboardLatest>("clipboard/latest")).ClipboardSequence; await ea.SyncNow();
    Check((await a.Get<ClipboardLatest>("clipboard/latest")).ClipboardSequence == seq, "offline queue not promoted by manual sync");
    config.Enabled = true; await Wait(() => ea.Connected, "reconnection");
    Console.WriteLine("PASS two-engine live copy, files, retry, lock and offline behavior");

    var deleted = await Apply(a, new(Guid.NewGuid().ToString(), "delete", duplicate.RecordId)); Check(deleted.Record?.DeletedAt is not null, "alias delete produces tombstone");
    Check((await Apply(b, new(Guid.NewGuid().ToString(), "capture", created.RecordId, record))).Status == "deleted", "stale capture cannot revive deleted record");
    var oldRefresh = outsider.Session!.RefreshToken; var refreshed = await outsider.Post<SessionTokens>("auth/refresh", new RefreshRequest(oldRefresh), authenticated: false);
    Check((await outsider.Post<SessionTokens>("auth/refresh", new RefreshRequest(oldRefresh), authenticated: false)).AccessToken == refreshed.AccessToken, "refresh retry returns same rotated session");
    await outsider.Command("auth/logout", new LogoutRequest(refreshed.RevokeToken), authenticated: false); outsider.SetSession(refreshed); await Expect(() => outsider.Get<ChangesResponse>("sync/changes?cursor=0"), 401);
    Console.WriteLine($"PASS {assertions} assertions");
}
finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); try { Directory.Delete(root, true); } catch (IOException) { } }
