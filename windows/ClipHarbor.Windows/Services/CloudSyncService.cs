using ClipHarbor.Core;
using ClipHarbor.Sync;
using Microsoft.UI.Dispatching;
using System.Runtime.InteropServices;
using System.Text;
using Windows.Storage;
using Windows.Graphics.Imaging;
using Windows.ApplicationModel.DataTransfer;

namespace ClipHarbor.Windows.Services;

internal sealed class CloudSyncService : IAsyncDisposable
{
    private readonly HistoryStore _store;
    private readonly ClipboardService _clipboard;
    private readonly DispatcherQueue _dispatcher;
    private SessionTokens? _session;
    private SyncEngine? _engine;
    private readonly CancellationTokenSource _life = new();
    private readonly SemaphoreSlim _revocationGate = new(1, 1);
    private Task? _revocations;
    public SyncConfig Config => _store.Settings.Sync;
    public string Status { get; private set; } = "未登录";
    public event Action? Changed;
    public PhraseLibrary Phrases { get; private set; } = new([], [], [], "登录后可以维护自己的短语", false);
    public string? PhraseSpace { get; private set; }
    public bool CanEditPhrases => PhraseSpace is not null && Phrases.Supported;
    public event Action? PhrasesChanged;
    private void ClearPhrases() { PhraseSpace = null; Phrases = new([], [], [], "登录后可以维护自己的短语", false); PhrasesChanged?.Invoke(); }
    public async Task SavePhrase(PhraseEntity entity, string space, PhraseEntity? newGroup = null, bool delete = false)
    {
        var engine = _engine; if (engine is null || PhraseSpace != space || engine.Space != space) throw new InvalidDataException("账号已改变，请重新打开短语。");
        await engine.Phrases.Save(entity, newGroup, delete);
    }
    public async Task ResolvePhrase(string id, string choice, string space)
    {
        var engine = _engine; if (engine is null || PhraseSpace != space || engine.Space != space) throw new InvalidDataException("账号已改变，请重新打开短语。");
        await engine.Phrases.Resolve(id, choice);
    }
    public Task SyncPhrasesNow() => _engine?.Phrases.SyncNow() ?? Task.CompletedTask;
    public string? DataDirectory => _engine?.Journal.DirectoryPath;
    public CloudSyncService(HistoryStore store, ClipboardService clipboard, DispatcherQueue dispatcher)
    {
        _store = store; _clipboard = clipboard; _dispatcher = dispatcher;
        _clipboard.Copied += Captured; _clipboard.HistoricalCapture += HistoricalCaptured;
        _store.SyncMutation += Mutated;
    }
    private Task Dispatch(Func<Task> callback)
    {
        if (_dispatcher.HasThreadAccess) return callback();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_dispatcher.TryEnqueue(async () => { try { await callback(); completion.SetResult(); } catch (Exception error) { completion.SetException(error); } })) completion.SetException(new InvalidOperationException("界面已关闭。"));
        return completion.Task;
    }
    private void SetStatus(string value) => _dispatcher.TryEnqueue(() => { Status = value; Changed?.Invoke(); });
    public async Task Initialize()
    {
        _revocations = RetryRevocations();
        try
        {
            if (Config.KeepSignedIn && Config.ServerUrl != "" && SyncCredentials.Read(Config.CredentialId) is { } json) _session = Protocol.Decode<SessionTokens>(json);
            if (_session is not null) _store.CaptureSyncSpace = Config.LastSpace;
            if (_session is not null) await Connect();
        }
        catch (Exception error) { SetStatus("同步未连接：" + error.Message); }
    }
    public static async Task Test(string server)
    {
        using var api = new SyncApi(Protocol.Server(server)); await api.Test();
    }
    public async Task Login(string server, string username, string password, string device, bool keepSignedIn, int importMode)
    {
        var config = Protocol.Decode<SyncConfig>(Protocol.Encode(Config));
        config.ServerUrl = Protocol.Server(server).AbsoluteUri.TrimEnd('/'); config.Username = username.Trim(); config.DeviceName = device.Trim(); config.KeepSignedIn = keepSignedIn; config.Enabled = true; config.CredentialId = Guid.NewGuid().ToString(); config.ServerInstanceId = null;
        using var api = new SyncApi(Protocol.Server(config.ServerUrl));
        var session = await api.Login(new(config.Username, password, config.DeviceName, config.DeviceId));
        ClearPhrases();
        config.ServerInstanceId = api.Metadata!.InstanceId;
        _store.CaptureSyncSpace = null;
        if (keepSignedIn) SyncCredentials.Write(config.CredentialId, Protocol.Encode(session));
        if (_engine is not null) { var oldEngine = _engine; _engine = null; await oldEngine.DisposeAsync(); }
        var oldCredential = Config.CredentialId; _store.Settings.Sync = config; _session = session;
        SyncCredentials.Delete(oldCredential); await _store.SaveAsync(); await Connect();
        if (importMode > 0)
            foreach (var item in _store.Items.Where(i => i.SyncSpace is null && (importMode == 2 || i.Favorite)).ToList()) await Queue(item, _clipboard.Version, false);
    }
    public async Task Configure(SyncConfig config)
    {
        if (_engine is not null) { var oldEngine = _engine; _engine = null; await oldEngine.DisposeAsync(); }
        _store.Settings.Sync = config; await _store.SaveAsync();
        if (!config.KeepSignedIn) SyncCredentials.Delete(config.CredentialId);
        else if (_session is not null) SyncCredentials.Write(config.CredentialId, Protocol.Encode(_session));
        if (_session is not null) await Connect();
    }
    private async Task Connect()
    {
        if (_session is null) return;
        var api = new SyncApi(Protocol.Server(Config.ServerUrl, Config.AllowLocalHttp), _session);
        api.PersistSession = next => { _session = next; if (Config.KeepSignedIn) SyncCredentials.Write(Config.CredentialId, Protocol.Encode(next)); return Task.CompletedTask; };
        try
        {
            var instance = Config.ServerInstanceId;
            if (!Guid.TryParse(instance, out _)) { instance = (await api.Test()).InstanceId; Config.ServerInstanceId = instance; }
            var engine = new SyncEngine(api, Config, _store.DirectoryPath, instance!)
            {
                ClipboardVersion = () => Task.FromResult(_clipboard.Version), Unlocked = () => Unlocked() && !_store.Paused,
                Bind = (wire, space, sourceId) => Dispatch(async () =>
                {
                    var item = _store.Items.FirstOrDefault(i => (i.SyncSpace is null || i.SyncSpace == space) && (i.SyncRecordId == sourceId || i.Id.ToString() == sourceId || i.SyncRecordId == wire.RecordId || i.SyncContentHash == wire.ContentHash || i.Id.ToString() == wire.RecordId));
                    if (item is null) return;
                    item.SyncSpace = space; item.SyncRecordId = wire.RecordId; item.SyncContentHash = wire.ContentHash;
                    if (wire.Revision > 0) { item.Favorite = wire.Favorite; item.Note = wire.Note; }
                    foreach (var duplicate in _store.Items.Where(i => i != item && i.SyncSpace == space && i.SyncRecordId == wire.RecordId).ToList())
                    {
                        item.CaptureCount = Math.Max(item.CaptureCount, duplicate.CaptureCount); item.UseCount = Math.Max(item.UseCount, duplicate.UseCount); _store.Items.Remove(duplicate);
                    }
                    await _store.SaveAsync();
                }),
                Receive = (received, direct, expected) => Dispatch(() => Receive(received, direct, expected))
            };
            _engine = engine;
            PhraseSpace = engine.Space; Phrases = engine.Phrases.Library(); PhrasesChanged?.Invoke();
            engine.Phrases.Changed = library => Dispatch(() => { if (_engine == engine && PhraseSpace == engine.Space) { Phrases = library; PhrasesChanged?.Invoke(); } return Task.CompletedTask; });
            engine.Status += value => { if (_engine == engine) SetStatus(value); }; _store.ActiveSyncSpace = _store.CaptureSyncSpace = engine.Space; Config.LastSpace = engine.Space;
            await _store.NotifyAsync(); engine.Start();
        }
        catch { api.Dispose(); throw; }
    }
    private void Captured(ClipRecord item, long version) { _ = Queue(item, version, true); }
    private void HistoricalCaptured(ClipRecord item) { _ = Queue(item, _clipboard.Version, false); }
    private async Task Queue(ClipRecord item, long version, bool real)
    {
        var engine = _engine;
        if (engine is null || item.SyncSpace is not null && item.SyncSpace != engine.Space) return;
        var wire = new WireRecord { RecordId = item.SyncRecordId ?? item.Id.ToString(), Kind = item.Kind.ToString().ToLowerInvariant(), Text = item.Text, RtfBase64 = item.RichText is null ? null : Convert.ToBase64String(Encoding.UTF8.GetBytes(item.RichText)), Html = item.Html, CapturedAt = item.CapturedAt.ToString("O"), LastCapturedAt = Protocol.Now, Favorite = item.Favorite, Note = item.Note };
        if (wire.Html is { } formatted)
        {
            try { wire.Html = HtmlFormatHelper.GetStaticFragment(formatted); } catch (ArgumentException) { wire.Html = null; }
        }
        var paths = item.ImageName is { } image ? new List<string> { _store.ImagePath(image) } : item.FilePaths.ToList();
        try { await engine.Capture(wire, paths, version, real); } catch (Exception error) { SetStatus("同步失败：" + error.Message); }
    }
    private async Task Receive(ReceivedRecord received, bool direct, long expected)
    {
        var engine = _engine; if (engine is null) return;
        var wire = received.Record;
        if (wire.DeletedAt is not null)
        {
            _store.Items.RemoveAll(i => i.SyncSpace == engine.Space && engine.Journal.Resolve(i.SyncRecordId ?? "") == wire.RecordId); await _store.NotifyAsync(); return;
        }
        var item = new ClipRecord { Id = Guid.Parse(wire.RecordId), Kind = Enum.Parse<ClipKind>(wire.Kind, true), Text = wire.Text, RichText = wire.RtfBase64 is null ? null : Encoding.UTF8.GetString(Convert.FromBase64String(wire.RtfBase64)), Html = wire.Html, FilePaths = received.FilePaths, CapturedAt = DateTimeOffset.Parse(wire.LastCapturedAt), Source = "另一台设备", Favorite = wire.Favorite, Note = wire.Note, SyncSpace = engine.Space, SyncRecordId = wire.RecordId, SyncContentHash = wire.ContentHash, CaptureCount = 0 };
        if (wire.Html is not null) item.Html = HtmlFormatHelper.CreateHtmlFormat(wire.Html);
        if (string.Equals(wire.OriginDeviceId, Config.DeviceId, StringComparison.OrdinalIgnoreCase)) item.Source = _store.Items.FirstOrDefault(i => i.SyncSpace == engine.Space && i.SyncRecordId == wire.RecordId)?.Source ?? "本机";
        foreach (var file in item.FilePaths) await File.WriteAllTextAsync(file + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\nHostUrl=" + engine.Api.BaseUri.AbsoluteUri + "\r\n");
        if (received.ImagePath is { } image)
        {
            using var stream = await (await StorageFile.GetFileFromPathAsync(image)).OpenReadAsync();
            var decoder = await BitmapDecoder.CreateAsync(stream);
            if ((ulong)decoder.PixelWidth * decoder.PixelHeight > 40_000_000) throw new InvalidDataException("接收图片尺寸过大。");
            using var decoded = await decoder.GetSoftwareBitmapAsync();
            item.ImageName = await _store.CacheImageAsync(await File.ReadAllBytesAsync(image));
        }
        if (!engine.Journal.Hidden(wire.RecordId)) await _store.ImportRemoteAsync(item);
        if (direct && Config.DirectPaste && Config.Enabled && Unlocked() && !_store.Paused) await _clipboard.CopyAsync(item, remote: true, expectedVersion: expected);
    }
    private void Mutated(ClipRecord item, string type)
    {
        if (_engine is not { } engine || item.SyncSpace != engine.Space || item.SyncRecordId is not { } id) return;
        if (type == "hide") engine.Hide(id); else if (type == "delete") engine.Delete(id);
    }
    public void MetadataChanged(ClipRecord item)
    {
        if (_engine is not { } engine || item.SyncSpace != engine.Space || item.SyncRecordId is not { } id || engine.Journal.Record(id) is not { } wire) return;
        if (item.Favorite != wire.Favorite) engine.Edit(id, "favorite", favorite: item.Favorite);
        if (item.Note != wire.Note) engine.Edit(id, "note", note: item.Note);
    }
    public Task SyncNow() => _engine?.SyncNow() ?? Task.CompletedTask;
    public string? Conflicts() => _engine?.Journal.ExportFailures();
    public async Task Logout()
    {
        ClearPhrases();
        var tokens = _session;
        if (_engine is not null) { var oldEngine = _engine; _engine = null; await oldEngine.DisposeAsync(); }
        _session = null; _store.CaptureSyncSpace = null; SyncCredentials.Delete(Config.CredentialId); Config.Enabled = false; await _store.SaveAsync();
        if (tokens is not null)
        {
            try
            {
                using var api = new SyncApi(Protocol.Server(Config.ServerUrl, Config.AllowLocalHttp));
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_life.Token); timeout.CancelAfter(TimeSpan.FromSeconds(15));
                await api.Command("auth/logout", new LogoutRequest(tokens.RevokeToken), timeout.Token, false);
            }
            catch
            {
                var id = "revoke-" + Guid.NewGuid(); SyncCredentials.Write(id, Protocol.Encode(new { serverUrl = Config.ServerUrl, revokeToken = tokens.RevokeToken }));
                await PendingRevocations(add: id);
            }
        }
        SetStatus("已退出登录，本机历史保留");
    }
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr desktop);
    private static bool Unlocked() { var desktop = OpenInputDesktop(0, false, 1); if (desktop == IntPtr.Zero) return false; CloseDesktop(desktop); return true; }
    private async Task<List<string>> PendingRevocations(string? add = null, string? remove = null)
    {
        await _revocationGate.WaitAsync();
        try
        {
            var path = Path.Combine(_store.DirectoryPath, "pending-revocations.json");
            var ids = File.Exists(path) ? Protocol.Decode<List<string>>(await File.ReadAllTextAsync(path)) : [];
            if (add is not null && !ids.Contains(add)) ids.Add(add);
            if (remove is not null) ids.RemoveAll(id => id == remove);
            if (add is not null || remove is not null)
            {
                var temporary = path + ".tmp";
                await File.WriteAllTextAsync(temporary, Protocol.Encode(ids)); File.Move(temporary, path, true);
            }
            return ids;
        }
        finally { _revocationGate.Release(); }
    }
    private async Task RetryRevocations()
    {
        while (!_life.IsCancellationRequested)
        {
            try
            {
                var ids = await PendingRevocations();
                foreach (var id in ids)
                {
                    try
                    {
                        if (SyncCredentials.Read(id) is { } json)
                        {
                            var revoke = Protocol.Decode<PendingRevocation>(json);
                            using var api = new SyncApi(Protocol.Server(revoke.ServerUrl)); using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_life.Token); timeout.CancelAfter(TimeSpan.FromSeconds(15));
                            await api.Command("auth/logout", new LogoutRequest(revoke.RevokeToken), timeout.Token, false);
                        }
                        await PendingRevocations(remove: id); SyncCredentials.Delete(id);
                    }
                    catch (Exception) when (!_life.IsCancellationRequested) { }
                }
            }
            catch (Exception) when (!_life.IsCancellationRequested) { }
            await Task.Delay(TimeSpan.FromMinutes(5), _life.Token);
        }
    }
    private sealed record PendingRevocation(string ServerUrl, string RevokeToken);
    public async ValueTask DisposeAsync()
    {
        _clipboard.Copied -= Captured; _clipboard.HistoricalCapture -= HistoricalCaptured; _store.SyncMutation -= Mutated;
        _life.Cancel(); if (_revocations is not null) try { await _revocations; } catch (OperationCanceledException) { }
        if (_engine is not null) { var oldEngine = _engine; _engine = null; await oldEngine.DisposeAsync(); }
        _life.Dispose();
    }
}
