using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using ClipHarbor.Sync;

namespace ClipHarbor.Server;

public sealed class EventHub(SyncStore store, Authentication authentication)
{
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<Guid, Channel<EventNotice>>> _listeners = new();
    public void Notify(Guid account, EventNotice notice)
    {
        if (_listeners.TryGetValue(account, out var listeners)) foreach (var channel in listeners.Values) channel.Writer.TryWrite(notice);
    }
    public async Task Connect(HttpContext context, AuthSession session)
    {
        if (!context.WebSockets.IsWebSocketRequest) throw new ApiException(400, "WEBSOCKET_REQUIRED", "需要 WebSocket 连接。");
        var listeners = _listeners.GetOrAdd(session.AccountId, _ => new());
        if (listeners.Count >= 32) throw new ApiException(429, "CONNECTION_LIMIT", "账号连接数量已达上限。");
        var channel = Channel.CreateBounded<EventNotice>(new BoundedChannelOptions(16) { FullMode = BoundedChannelFullMode.DropOldest });
        var connectionId = Guid.NewGuid(); listeners[connectionId] = channel;
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        try
        {
            var latest = await store.Latest(session);
            async Task Send(EventNotice notice) => await socket.SendAsync(Encoding.UTF8.GetBytes(Protocol.Encode(notice)), WebSocketMessageType.Text, true, lifetime.Token);
            await Send(new("hello", latest.ClipboardSequence, latest.SyncEpoch));
            var receiving = Task.Run(async () =>
            {
                var buffer = new byte[256];
                try { while (socket.State == WebSocketState.Open) { var result = await socket.ReceiveAsync(buffer, lifetime.Token); if (result.MessageType == WebSocketMessageType.Close || !result.EndOfMessage || result.Count > 0) break; } }
                finally { lifetime.Cancel(); }
            });
            while (!lifetime.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                using var interval = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); interval.CancelAfter(TimeSpan.FromSeconds(10));
                try { await Send(await channel.Reader.ReadAsync(interval.Token)); }
                catch (OperationCanceledException) when (!lifetime.IsCancellationRequested)
                {
                    if (!await authentication.Valid(session)) break;
                    latest = await store.Latest(session); await Send(new("heartbeat", latest.ClipboardSequence, latest.SyncEpoch));
                }
            }
            lifetime.Cancel();
            await receiving;
        }
        catch (Exception error) when (error is OperationCanceledException or WebSocketException) { }
        finally { listeners.TryRemove(connectionId, out _); lifetime.Cancel(); }
    }
}
