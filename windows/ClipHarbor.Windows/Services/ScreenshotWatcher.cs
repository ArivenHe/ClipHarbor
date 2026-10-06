using ClipHarbor.Core;
using Microsoft.UI.Dispatching;
using ClipHarbor.Windows.Interop;
using System.Collections.Concurrent;

namespace ClipHarbor.Windows.Services;

internal sealed class ScreenshotWatcher : IDisposable
{
    private readonly HistoryStore _store;
    private readonly ClipboardService _clipboard;
    private readonly DispatcherQueue _dispatcher;
    private readonly ConcurrentDictionary<string, byte> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _cancel = new();
    private readonly CancellationToken _token;
    private FileSystemWatcher? _watcher;
    public string Status { get; private set; } = "截图监听已关闭";
    public ScreenshotWatcher(HistoryStore store, ClipboardService clipboard, DispatcherQueue dispatcher)
    {
        _store = store; _clipboard = clipboard; _dispatcher = dispatcher; _token = _cancel.Token;
        if (!store.Settings.WatchScreenshots) return;
        var path = string.IsNullOrWhiteSpace(store.Settings.ScreenshotFolder) ? NativeMethods.ScreenshotsDirectory() : store.Settings.ScreenshotFolder;
        try
        {
            Directory.CreateDirectory(path);
            _watcher = new(path) { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size, IncludeSubdirectories = false };
            _watcher.Created += OnFile; _watcher.Changed += OnFile;
            _watcher.Renamed += (_, e) => Queue(e.FullPath);
            _watcher.Error += (_, _) => Status = "截图目录监听中断，请在设置中重新保存监听位置。";
            _watcher.EnableRaisingEvents = true;
            Status = "正在监听：" + path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { Status = "无法监听截图目录：" + e.Message; }
    }
    private void OnFile(object sender, FileSystemEventArgs args) => Queue(args.FullPath);
    private void Queue(string path)
    {
        if (_store.Paused || FileTypes.Classify(path) != FileCategory.Image || !_pending.TryAdd(path, 0)) return;
        _ = WaitForStableFileAsync(path);
    }
    private async Task WaitForStableFileAsync(string path)
    {
        try
        {
            long size = -1;
            var stable = 0;
            for (var attempt = 0; attempt < 16; attempt++)
            {
                await Task.Delay(300, _token);
                var current = new FileInfo(path).Length;
                stable = current > 0 && current == size ? stable + 1 : 0;
                size = current;
                if (stable < 2) continue;
                if (!_token.IsCancellationRequested && !_store.Paused) _dispatcher.TryEnqueue(async () => { if (!_token.IsCancellationRequested) await _clipboard.ImportScreenshotAsync(path); });
                return;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or OperationCanceledException) { }
        finally { _pending.TryRemove(path, out _); }
    }
    public void Dispose() { _cancel.Cancel(); _watcher?.Dispose(); _cancel.Dispose(); }
}
