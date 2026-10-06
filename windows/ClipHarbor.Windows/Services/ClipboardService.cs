using ClipHarbor.Core;
using ClipHarbor.Windows.Interop;
using Microsoft.UI.Dispatching;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace ClipHarbor.Windows.Services;

internal sealed class ClipboardService : IDisposable
{
    private readonly HistoryStore _store;
    private readonly DispatcherQueue _dispatcher;
    private uint _lastSequence;
    private bool _capturing, _pending, _disposed;
    public event Action<string>? Error;
    public ClipboardService(HistoryStore store, DispatcherQueue dispatcher)
    {
        _store = store; _dispatcher = dispatcher;
        _lastSequence = NativeMethods.GetClipboardSequenceNumber();
        Clipboard.ContentChanged += OnChanged;
    }
    private void OnChanged(object? sender, object e) => _dispatcher.TryEnqueue(CapturePending);
    private async void CapturePending()
    {
        _pending = true;
        if (_capturing || _disposed) return;
        _capturing = true;
        try
        {
            while (_pending && !_disposed)
            {
                _pending = false;
                for (var attempt = 0; attempt < 3; attempt++)
                {
                    try { await CaptureAsync(); break; }
                    catch (System.Runtime.InteropServices.COMException) when (attempt < 2) { await Task.Delay(70); }
                }
            }
        }
        catch (Exception e) { Error?.Invoke("读取剪贴板失败：" + e.Message); }
        finally { _capturing = false; }
    }
    private async Task CaptureAsync()
    {
        var sequence = NativeMethods.GetClipboardSequenceNumber();
        if (sequence == _lastSequence || _store.Paused || _disposed) { _lastSequence = sequence; return; }
        var source = NativeMethods.ProcessName(NativeMethods.GetClipboardOwner());
        if (string.IsNullOrEmpty(source)) source = NativeMethods.ProcessName(NativeMethods.GetForegroundWindow());
        var data = Clipboard.GetContent();
        if (_store.Settings.IsExcluded(source)) { _lastSequence = sequence; return; }
        if (data.AvailableFormats.Any(f => f is "ExcludeClipboardContentFromMonitorProcessing" or "org.nspasteboard.ConcealedType" or "org.nspasteboard.TransientType")) { _lastSequence = sequence; return; }
        if (data.Contains("CanIncludeInClipboardHistory"))
        {
            var value = await data.GetDataAsync("CanIncludeInClipboardHistory");
            if (value is false or 0 || value is byte[] bytes && bytes.All(b => b == 0)) { _lastSequence = sequence; return; }
        }
        ClipRecord? record = null;
        if (data.Contains(StandardDataFormats.StorageItems))
        {
            var files = await data.GetStorageItemsAsync();
            var paths = files.Select(f => f.Path).Where(p => !string.IsNullOrEmpty(p)).ToList();
            if (paths.Count > 0)
            {
                var references = new ClipRecord { Kind = ClipKind.Files, FilePaths = paths };
                if (_store.Settings.ShouldRecord(references.DisplayKind)) record = references;
            }
        }
        else if (_store.Settings.RecordImages && data.Contains(StandardDataFormats.Bitmap))
        {
            using var stream = await (await data.GetBitmapAsync()).OpenReadAsync();
            record = new() { Kind = ClipKind.Image, ImageName = await _store.CacheImageAsync(await EncodePngAsync(stream)) };
        }
        else if (_store.Settings.RecordText && data.Contains(StandardDataFormats.Text))
        {
            var text = await data.GetTextAsync();
            if (System.Text.Encoding.UTF8.GetByteCount(text) > 2 * 1024 * 1024) throw new InvalidDataException("文本超过 2 MB，未记录。");
            if (!string.IsNullOrWhiteSpace(text))
            {
                var link = Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";
                record = new() { Kind = link ? ClipKind.Link : ClipKind.Text, Text = text };
                if (data.Contains(StandardDataFormats.Rtf)) record.RichText = await data.GetRtfAsync();
                if (data.Contains(StandardDataFormats.Html)) record.Html = await data.GetHtmlFormatAsync();
                if ((record.RichText?.Length ?? 0) + (record.Html?.Length ?? 0) > 4 * 1024 * 1024) { record.RichText = null; record.Html = null; }
            }
        }
        if (sequence != NativeMethods.GetClipboardSequenceNumber()) { _pending = true; return; }
        _lastSequence = sequence;
        if (record is null || _store.Paused || _disposed) return;
        record.Source = source;
        await _store.AddAsync(record);
    }
    private static async Task<byte[]> EncodePngAsync(IRandomAccessStream stream)
    {
        var decoder = await BitmapDecoder.CreateAsync(stream);
        if ((ulong)decoder.PixelWidth * decoder.PixelHeight > 40_000_000) throw new InvalidDataException("图片尺寸过大，未记录。");
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        using var encoded = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, encoded);
        encoder.SetSoftwareBitmap(bitmap); await encoder.FlushAsync();
        if (encoded.Size > 100 * 1024 * 1024) throw new InvalidDataException("图片编码超过大小限制。");
        encoded.Seek(0);
        using var reader = new DataReader(encoded.GetInputStreamAt(0));
        await reader.LoadAsync((uint)encoded.Size);
        var png = new byte[(int)encoded.Size]; reader.ReadBytes(png);
        return png;
    }
    public async Task ImportScreenshotAsync(string path)
    {
        if (_store.Paused || !_store.Settings.WatchScreenshots || _disposed) return;
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            using var stream = await file.OpenReadAsync();
            var name = await _store.CacheImageAsync(await EncodePngAsync(stream));
            if (_store.Paused || _disposed) return;
            await _store.AddAsync(new() { Kind = ClipKind.Image, ImageName = name, ScreenshotPath = path, Source = "Windows 截图" });
        }
        catch (Exception e) { Error?.Invoke("保存截图失败：" + e.Message); }
    }
    public async Task CopyAsync(ClipRecord item, bool plain = false)
    {
        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        if (item.Kind == ClipKind.Files)
        {
            var files = new List<IStorageItem>();
            foreach (var path in item.FilePaths)
            {
                if (Directory.Exists(path)) files.Add(await StorageFolder.GetFolderFromPathAsync(path));
                else if (File.Exists(path)) files.Add(await StorageFile.GetFileFromPathAsync(path));
                else throw new FileNotFoundException("原文件不可用，请检查文件位置。", path);
            }
            package.SetStorageItems(files);
        }
        else if (item.ImageName is { } name)
        {
            var file = await StorageFile.GetFileFromPathAsync(_store.ImagePath(name));
            package.SetBitmap(RandomAccessStreamReference.CreateFromFile(file));
        }
        else
        {
            package.SetText(item.Text ?? "");
            if (!plain && !_store.Settings.PlainText)
            {
                if (item.RichText is { } rich) package.SetRtf(rich);
                if (item.Html is { } html) package.SetHtmlFormat(html);
            }
        }
        Clipboard.SetContent(package);
        _lastSequence = NativeMethods.GetClipboardSequenceNumber();
        Clipboard.Flush();
        _lastSequence = NativeMethods.GetClipboardSequenceNumber();
        await _store.RecordUseAsync(item);
    }
    public void Dispose() { _disposed = true; Clipboard.ContentChanged -= OnChanged; }
}
