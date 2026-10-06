using ClipHarbor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Data.Pdf;
using Windows.Media.Core;
using Windows.Storage;
using Windows.Storage.Streams;

namespace ClipHarbor.Windows.Services;

internal static class PreviewService
{
    public static async Task<UIElement> CreateAsync(string path, CancellationToken token)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return Message("原文件不可用，请检查文件位置。");
        if (Directory.Exists(path))
        {
            var names = Directory.EnumerateFileSystemEntries(path).Take(30).Select(Path.GetFileName);
            return Message(string.Join(Environment.NewLine, names));
        }
        var file = await StorageFile.GetFileFromPathAsync(path);
        token.ThrowIfCancellationRequested();
        var category = FileTypes.Classify(path);
        if (category == FileCategory.Image)
        {
            using var stream = await file.OpenReadAsync();
            var bitmap = new BitmapImage(); await bitmap.SetSourceAsync(stream);
            token.ThrowIfCancellationRequested();
            var image = new Image { Source = bitmap, Stretch = Stretch.Uniform, MaxHeight = 640, HorizontalAlignment = HorizontalAlignment.Stretch };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(image, file.Name);
            return image;
        }
        if (Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            var document = await PdfDocument.LoadFromFileAsync(file);
            token.ThrowIfCancellationRequested();
            var preview = new PdfPreview(document);
            await preview.RenderAsync(0);
            return preview;
        }
        if (category is FileCategory.Audio or FileCategory.Video)
        {
            var player = new MediaPlayerElement { Source = MediaSource.CreateFromStorageFile(file), AreTransportControlsEnabled = true, AutoPlay = false, MaxHeight = 480 };
            player.Unloaded += (_, _) => { player.MediaPlayer?.Pause(); player.MediaPlayer?.Dispose(); };
            return player;
        }
        if (category == FileCategory.Code || new[] { ".txt", ".md", ".csv", ".tsv" }.Contains(Path.GetExtension(path).ToLowerInvariant()))
        {
            using var reader = new StreamReader(path);
            var buffer = new char[32768];
            var length = await reader.ReadBlockAsync(buffer.AsMemory(), token);
            return Message(new string(buffer, 0, length) + (reader.EndOfStream ? "" : "\n\n仅显示前 32K 字符。"));
        }
        return Message($"{file.Name}\n{new FileInfo(path).Length:N0} 字节\n\n此格式可通过“打开原文件”在关联应用中查看。");
    }
    private static TextBlock Message(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
}

internal sealed class PdfPreview : StackPanel
{
    private readonly PdfDocument _document;
    private readonly Image _image = new() { Stretch = Stretch.Uniform, MaxHeight = 640 };
    private readonly TextBlock _pageLabel = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _previous = new() { Content = "上一页" };
    private readonly Button _next = new() { Content = "下一页" };
    private uint _page;
    private bool _rendering;
    public PdfPreview(PdfDocument document)
    {
        _document = document; Spacing = 8;
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        controls.Children.Add(_previous); controls.Children.Add(_pageLabel); controls.Children.Add(_next);
        Children.Add(controls); Children.Add(_image);
        _previous.Click += async (_, _) => { if (!_rendering && _page > 0) await RenderAsync(_page - 1); };
        _next.Click += async (_, _) => { if (!_rendering && _page + 1 < _document.PageCount) await RenderAsync(_page + 1); };
    }
    public async Task RenderAsync(uint index)
    {
        _rendering = true; _previous.IsEnabled = _next.IsEnabled = false;
        try
        {
            using var page = _document.GetPage(index);
            using var stream = new InMemoryRandomAccessStream();
            await page.RenderToStreamAsync(stream, new PdfPageRenderOptions { DestinationWidth = 1000 });
            var bitmap = new BitmapImage(); await bitmap.SetSourceAsync(stream);
            _image.Source = bitmap; _page = index;
            _pageLabel.Text = $"{index + 1} / {_document.PageCount}";
        }
        finally { _rendering = false; _previous.IsEnabled = _page > 0; _next.IsEnabled = _page + 1 < _document.PageCount; }
    }
}
