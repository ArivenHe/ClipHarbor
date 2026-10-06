using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;

namespace ClipHarbor.Sync;

public sealed class SyncApi : IDisposable
{
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _refresh = new(1, 1);
    public Uri BaseUri { get; }
    public SessionTokens? Session { get; private set; }
    public ServerMeta? Metadata { get; private set; }
    public Func<SessionTokens, Task>? PersistSession { get; set; }
    public Action<long, long>? Progress { get; set; }
    public SyncApi(Uri server, SessionTokens? session = null, HttpMessageHandler? handler = null)
    {
        BaseUri = server; Session = session;
        _http = new(handler ?? new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = server, Timeout = TimeSpan.FromMinutes(5) };
    }
    public void SetSession(SessionTokens? session) => Session = session;
    public async Task<ServerMeta> Test(CancellationToken token = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var meta = await Get<ServerMeta>("meta", timeout.Token, false);
        if (meta.Product != "ClipHarbor.Sync" || meta.ProtocolVersion != Protocol.Version || !Guid.TryParse(meta.InstanceId, out _)) throw new InvalidDataException("服务器不是兼容的 ClipHarbor 同步服务。");
        return Metadata = meta;
    }
    public async Task<SessionTokens> Login(LoginRequest request, CancellationToken token = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(30)); token = timeout.Token;
        await Test(token);
        var session = await Post<SessionTokens>("auth/login", request, token, false);
        if (PersistSession is not null) await PersistSession(session);
        return Session = session;
    }
    public async Task EnsureSession(CancellationToken token = default)
    {
        if (Session is null) throw new SyncHttpException(401, "SESSION_EXPIRED", "请重新登录。");
        if (DateTimeOffset.Parse(Session.AccessExpiresAt) <= DateTimeOffset.UtcNow.AddSeconds(30)) await Refresh(Session.AccessToken, token);
    }
    private async Task Refresh(string previousAccess, CancellationToken token)
    {
        await _refresh.WaitAsync(token);
        try
        {
            if (Session is null) throw new SyncHttpException(401, "SESSION_EXPIRED", "请重新登录。");
            if (Session.AccessToken != previousAccess) return;
            var next = await Post<SessionTokens>("auth/refresh", new RefreshRequest(Session.RefreshToken), token, false);
            if (PersistSession is not null) await PersistSession(next);
            Session = next;
        }
        finally { _refresh.Release(); }
    }
    public async Task<T> Get<T>(string path, CancellationToken token = default, bool authenticated = true)
    {
        using var response = await Send(() => new(HttpMethod.Get, "api/v1/" + path), authenticated, token);
        return Protocol.Decode<T>(await response.Content.ReadAsStringAsync(token));
    }
    public async Task<T> Post<T>(string path, object value, CancellationToken token = default, bool authenticated = true)
    {
        using var response = await Send(() => new(HttpMethod.Post, "api/v1/" + path) { Content = new StringContent(Protocol.Encode(value), Encoding.UTF8, "application/json") }, authenticated, token);
        return Protocol.Decode<T>(await response.Content.ReadAsStringAsync(token));
    }
    public async Task Command(string path, object value, CancellationToken token = default, bool authenticated = true)
    {
        using var response = await Send(() => new(HttpMethod.Post, "api/v1/" + path) { Content = new StringContent(Protocol.Encode(value), Encoding.UTF8, "application/json") }, authenticated, token);
    }
    public async Task Delete(string path, CancellationToken token = default)
    {
        using var response = await Send(() => new(HttpMethod.Delete, "api/v1/" + path), true, token);
    }
    public async Task<HttpResponseMessage> Send(Func<HttpRequestMessage> factory, bool authenticated, CancellationToken token)
    {
        if (authenticated) await EnsureSession(token);
        for (var attempt = 0; ; attempt++)
        {
            using var request = factory(); var access = Session?.AccessToken;
            if (authenticated) request.Headers.Authorization = new("Bearer", access);
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if (response.StatusCode == HttpStatusCode.Unauthorized && authenticated && attempt == 0)
            {
                response.Dispose(); await Refresh(access!, token); continue;
            }
            if (response.IsSuccessStatusCode) return response;
            ApiError error;
            try { error = Protocol.Decode<ApiError>(await response.Content.ReadAsStringAsync(token)); }
            catch { error = new("HTTP_ERROR", "服务器请求失败，请检查地址和连接状态。"); }
            var status = (int)response.StatusCode; response.Dispose(); throw new SyncHttpException(status, error.Code, error.Message);
        }
    }
    public async Task<string> Upload(string path, string kind, string sha256, CancellationToken token)
    {
        var file = new FileInfo(path);
        var upload = await Post<UploadResponse>("uploads", new UploadRequest(kind, file.Length, sha256), token);
        if (upload.BlobId is not null) return upload.BlobId;
        await using var stream = File.OpenRead(path);
        var count = (file.Length + upload.PartBytes - 1) / upload.PartBytes;
        var buffer = new byte[upload.PartBytes];
        for (var index = 0; index < count; index++)
        {
            var expected = (int)Math.Min(upload.PartBytes, file.Length - (long)index * upload.PartBytes);
            stream.Position = (long)index * upload.PartBytes;
            if (!upload.ReceivedParts.Contains((int)index))
            {
                await stream.ReadExactlyAsync(buffer.AsMemory(0, expected), token);
                using var response = await Send(() => new(HttpMethod.Put, $"api/v1/uploads/{upload.UploadId}/parts/{index}") { Content = new ByteArrayContent(buffer, 0, expected) }, true, token);
            }
            Progress?.Invoke(Math.Min(file.Length, (index + 1L) * upload.PartBytes), file.Length);
        }
        return (await Post<UploadResponse>($"uploads/{upload.UploadId}/complete", new { }, token)).BlobId ?? throw new InvalidDataException("附件上传尚未完成。");
    }
    public async Task Download(string blobId, string sha256, string target, long limit, CancellationToken token)
    {
        if (!Guid.TryParse(blobId, out _) || !Protocol.IsHash(sha256)) throw new InvalidDataException("附件引用无效。");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (File.Exists(target))
        {
            await using var input = File.OpenRead(target);
            if (Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(input, token)).ToLowerInvariant() == sha256) return;
            File.Delete(target);
        }
        var partial = target + ".part"; var start = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        if (start > 0)
        {
            await using var existing = File.OpenRead(partial);
            if (Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(existing, token)).ToLowerInvariant() == sha256)
            { existing.Close(); File.Move(partial, target, true); return; }
        }
        if (start > limit) { File.Delete(partial); start = 0; }
        using var response = await Send(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "api/v1/blobs/" + blobId);
            if (start > 0) { request.Headers.Range = new(start, null); request.Headers.IfRange = new(new EntityTagHeaderValue('"' + sha256 + '"')); }
            return request;
        }, true, token);
        if (response.StatusCode != HttpStatusCode.PartialContent) start = 0;
        if (response.Content.Headers.ContentLength is long remaining && remaining + start > limit) throw new InvalidDataException("附件超过接收限制。");
        await using (var output = new FileStream(partial, start == 0 ? FileMode.Create : FileMode.Append, FileAccess.Write, FileShare.None, 65536, true))
        await using (var input = await response.Content.ReadAsStreamAsync(token))
        {
            var buffer = new byte[65536]; int count;
            while ((count = await input.ReadAsync(buffer, token)) != 0)
            {
                if (output.Length + count > limit) throw new InvalidDataException("附件超过接收限制。");
                await output.WriteAsync(buffer.AsMemory(0, count), token); Progress?.Invoke(output.Length, response.Content.Headers.ContentLength.GetValueOrDefault() + start);
            }
        }
        await using (var input = File.OpenRead(partial))
            if (Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(input, token)).ToLowerInvariant() != sha256) { File.Delete(partial); throw new InvalidDataException("附件完整性校验失败。"); }
        File.Move(partial, target, true);
    }
    public async Task<ClientWebSocket> Connect(CancellationToken token)
    {
        await EnsureSession(token);
        var uri = new UriBuilder(new Uri(BaseUri, "api/v1/events")) { Scheme = BaseUri.Scheme == "https" ? "wss" : "ws" };
        var socket = new ClientWebSocket(); socket.Options.SetRequestHeader("Authorization", "Bearer " + Session!.AccessToken); socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try { await socket.ConnectAsync(uri.Uri, timeout.Token); return socket; } catch { socket.Dispose(); throw; }
    }
    public void Dispose() { _http.Dispose(); _refresh.Dispose(); }
}

public sealed class SyncHttpException(int status, string code, string message) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}
