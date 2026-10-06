using ClipHarbor.Sync;
using System.Collections.Concurrent;
using System.Text.Json;

// Private inherited pipes only. No listener, shell arguments, or credential log.
var bridge = new NativeBridge();
await bridge.Run();

sealed class NativeBridge
{
    readonly SemaphoreSlim output = new(1, 1), commands = new(1, 1);
    readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> calls = new();
    readonly CancellationTokenSource life = new();
    readonly ConcurrentDictionary<string, Task> running = new();
    SyncEngine? engine; SessionTokens? session; SyncConfig config = new(); string root = "";
    long clipboardVersion; volatile bool unlocked;
    async Task Write(object value)
    {
        await output.WaitAsync();
        try { await Console.Out.WriteLineAsync(Protocol.Encode(value)); await Console.Out.FlushAsync(); }
        finally { output.Release(); }
    }
    async Task<JsonElement> Call(string method, object value)
    {
        var id = Guid.NewGuid().ToString(); var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously); calls[id] = completion;
        try { await Write(new { callId = id, method, @params = value }); return await completion.Task.WaitAsync(TimeSpan.FromSeconds(30), life.Token); }
        finally { calls.TryRemove(id, out _); }
    }
    async Task Persist(SessionTokens next)
    {
        await Call("persist", new { config.CredentialId, config.KeepSignedIn, session = next }); session = next;
    }
    async Task State()
    {
        var state = await Call("clipboardState", new { });
        Interlocked.Exchange(ref clipboardVersion, state.GetProperty("version").GetInt64()); unlocked = state.GetProperty("unlocked").GetBoolean();
    }
    async Task Connect()
    {
        if (session is null) return;
        var api = new SyncApi(Protocol.Server(config.ServerUrl, config.AllowLocalHttp), session) { PersistSession = Persist };
        try
        {
            var instance = config.ServerInstanceId;
            if (!Guid.TryParse(instance, out _)) { instance = (await api.Test(life.Token)).InstanceId; config.ServerInstanceId = instance; }
            await State();
            var next = new SyncEngine(api, config, root, instance!)
            {
                ClipboardVersion = async () => { await State(); return Interlocked.Read(ref clipboardVersion); },
                Unlocked = () => unlocked,
                Bind = async (record, space, sourceId) => { await Call("bind", new { record, space, sourceId }); },
                Receive = async (received, direct, expected) => { await Call("receive", new { received, direct, expected, space = engine!.Space, hidden = engine.Journal.Hidden(received.Record.RecordId) }); }
            };
            engine = next;
            await Call("configuration", new { config });
            next.Status += value => { _ = Write(new { method = "status", @params = new { value } }); };
            await Call("space", new { space = next.Space }); next.Start();
        }
        catch { api.Dispose(); throw; }
    }
    public async Task Run()
    {
        try
        {
            while (await Console.In.ReadLineAsync(life.Token) is { } line)
            {
                if (line.Length > 4 * 1024 * 1024) throw new InvalidDataException("Native message exceeds limit.");
                using var json = JsonDocument.Parse(line); var message = json.RootElement.Clone();
                if (message.TryGetProperty("callId", out var call))
                {
                    if (calls.TryGetValue(call.GetString()!, out var completion))
                    {
                        if (message.TryGetProperty("error", out var error)) completion.TrySetException(new InvalidDataException(error.GetString()));
                        else completion.TrySetResult(message.GetProperty("result").Clone());
                    }
                    continue;
                }
                var id = message.GetProperty("id").GetString()!;
                var task = Handle(message); running[id] = task;
                _ = task.ContinueWith(completed => running.TryRemove(id, out _));
            }
        }
        finally
        {
            life.Cancel(); foreach (var completion in calls.Values) completion.TrySetCanceled();
            if (engine is not null) await engine.DisposeAsync();
            try { await Task.WhenAll(running.Values); } catch (OperationCanceledException) { }
        }
    }
    async Task Handle(JsonElement message)
    {
        var id = message.GetProperty("id").GetString()!;
        await commands.WaitAsync();
        try
        {
            var p = message.GetProperty("params"); var method = message.GetProperty("method").GetString(); object result = new { };
            switch (method)
            {
                case "initialize":
                case "configure":
                    if (engine is not null) { await engine.DisposeAsync(); engine = null; }
                    var previous = config.CredentialId;
                    config = Protocol.Decode<SyncConfig>(p.GetProperty("config").GetRawText()); root = p.GetProperty("root").GetString()!;
                    if (method == "initialize" || previous != config.CredentialId) session = p.TryGetProperty("session", out var saved) && saved.ValueKind != JsonValueKind.Null ? Protocol.Decode<SessionTokens>(saved.GetRawText()) : null;
                    if (session is not null) { await Persist(session); await Connect(); }
                    break;
                case "test":
                    using (var api = new SyncApi(Protocol.Server(p.GetProperty("server").GetString()!))) { await api.Test(life.Token); }
                    break;
                case "login":
                    var next = Protocol.Decode<SyncConfig>(p.GetProperty("config").GetRawText()); next.ServerUrl = Protocol.Server(next.ServerUrl).AbsoluteUri.TrimEnd('/'); next.Enabled = true; next.CredentialId = Guid.NewGuid().ToString(); next.ServerInstanceId = null;
                    using (var api = new SyncApi(Protocol.Server(next.ServerUrl)))
                    {
                        var tokens = await api.Login(new(next.Username, p.GetProperty("password").GetString()!, next.DeviceName, next.DeviceId), life.Token);
                        next.ServerInstanceId = api.Metadata!.InstanceId;
                        if (engine is not null) { await engine.DisposeAsync(); engine = null; }
                        config = next; await Persist(tokens); await Connect(); result = new { config };
                    }
                    break;
                case "capture":
                    if (engine is not null)
                    {
                        var wire = Protocol.Decode<WireRecord>(p.GetProperty("record").GetRawText());
                        await engine.Capture(wire, Protocol.Decode<List<string>>(p.GetProperty("paths").GetRawText()), p.GetProperty("version").GetInt64(), p.GetProperty("real").GetBoolean());
                    }
                    break;
                case "mutate":
                    if (engine is not null)
                    {
                        var recordId = p.GetProperty("recordId").GetString()!; var type = p.GetProperty("type").GetString()!;
                        if (type == "hide") engine.Hide(recordId);
                        else if (type == "delete") engine.Delete(recordId);
                        else engine.Edit(recordId, type, p.TryGetProperty("favorite", out var favorite) ? favorite.GetBoolean() : null, p.TryGetProperty("note", out var note) ? note.GetString() : null);
                    }
                    break;
                case "sync": if (engine is not null) await engine.SyncNow(); break;
                case "conflicts": result = new { path = engine?.Journal.ExportFailures() }; break;
                case "revoke":
                    using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(life.Token))
                    using (var api = new SyncApi(Protocol.Server(p.GetProperty("server").GetString()!)))
                    {
                        timeout.CancelAfter(TimeSpan.FromSeconds(15));
                        await api.Command("auth/logout", new LogoutRequest(p.GetProperty("revokeToken").GetString()), timeout.Token, false);
                    }
                    break;
                case "logout":
                    if (engine is not null) { await engine.DisposeAsync(); engine = null; }
                    var old = session; session = null; config.Enabled = false;
                    result = new { config, revokeToken = old?.RevokeToken, server = config.ServerUrl };
                    await Write(new { method = "status", @params = new { value = "已退出登录，本机历史保留" } });
                    break;
                default: throw new InvalidDataException("Unknown native method.");
            }
            await Write(new { id, result });
        }
        catch (Exception error) { await Write(new { id, error = error is OperationCanceledException ? "同步操作已取消。" : error.Message }); }
        finally { commands.Release(); }
    }
}
