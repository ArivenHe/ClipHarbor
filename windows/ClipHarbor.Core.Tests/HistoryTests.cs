using ClipHarbor.Core;
using Xunit;

namespace ClipHarbor.Core.Tests;

public sealed class HistoryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ClipHarborTests-" + Guid.NewGuid());
    private HistoryStore Store() => new(_directory);
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }

    [Theory]
    [InlineData("2.png")]
    [InlineData("PHOTO.JPG")]
    [InlineData("photo.heic")]
    public void ImageReferencesKeepFileTransport(string name)
    {
        var item = new ClipRecord { Kind = ClipKind.Files, FilePaths = [name] };
        Assert.Equal(ClipKind.Image, item.DisplayKind);
        Assert.Equal(ClipKind.Files, item.Kind);
        Assert.Equal(name, item.Title);
    }
    [Fact]
    public void MixedSelectionKeepsFilesCategory()
    {
        Assert.Equal(ClipKind.Files, new ClipRecord { Kind = ClipKind.Files, FilePaths = ["photo.png", "report.pdf"] }.DisplayKind);
    }
    [Fact]
    public async Task DeduplicationPreservesFavoriteNoteAndUseCounts()
    {
        var store = Store(); await store.LoadAsync();
        var original = new ClipRecord { Kind = ClipKind.Text, Text = "hello", Note = "note", Favorite = true, UseCount = 3 };
        await store.AddAsync(original);
        await store.AddAsync(new() { Kind = ClipKind.Text, Text = "hello", Source = "Notepad" });
        var restored = Store(); await restored.LoadAsync();
        var item = Assert.Single(restored.Items);
        Assert.Equal(original.Id, item.Id);
        Assert.True(item.Favorite);
        Assert.Equal("note", item.Note);
        Assert.Equal(2, item.CaptureCount);
        Assert.Equal(3, item.UseCount);
        Assert.Equal("Notepad", item.Source);
    }
    [Fact]
    public async Task RichTextDifferencesAreNotDeduplicated()
    {
        var store = Store(); await store.LoadAsync();
        await store.AddAsync(new() { Kind = ClipKind.Text, Text = "hello" });
        await store.AddAsync(new() { Kind = ClipKind.Text, Text = "hello", RichText = "{\\rtf1 hello}" });
        Assert.Equal(2, store.Items.Count);
    }
    [Fact]
    public async Task ExpirationUsesImageCategoryAndDoesNotConsumeCapacity()
    {
        var store = Store(); await store.LoadAsync();
        store.Settings.HistoryLimit = 1;
        store.Settings.TypeRetention[ClipKind.Image] = new() { Override = true, Value = 1, Unit = "minutes" };
        var now = DateTimeOffset.UtcNow;
        store.Items.AddRange([
            new() { Kind = ClipKind.Files, FilePaths = ["2.png"], CapturedAt = now.AddMinutes(-1) },
            new() { Kind = ClipKind.Text, Text = "keep", CapturedAt = now.AddSeconds(-20) }
        ]);
        store.Prune(now);
        Assert.Equal("keep", Assert.Single(store.Items).Text);
    }
    [Fact]
    public async Task ClearAndCacheCleanupNeverDeleteOriginalFiles()
    {
        var store = Store(); await store.LoadAsync();
        var original = Path.Combine(_directory, "original.png"); await File.WriteAllBytesAsync(original, [1, 2, 3]);
        var cache = await store.CacheImageAsync([1, 2, 3]);
        await store.AddAsync(new() { Kind = ClipKind.Image, ImageName = cache });
        await store.AddAsync(new() { Kind = ClipKind.Files, FilePaths = [original], Favorite = true });
        await store.ClearAsync(true);
        Assert.True(File.Exists(original));
        Assert.False(File.Exists(store.ImagePath(cache)));
        Assert.True(Assert.Single(store.Items).Favorite);
    }
    [Fact]
    public async Task CorruptHistoryIsPreservedForRecovery()
    {
        Directory.CreateDirectory(_directory);
        Directory.CreateDirectory(Path.Combine(_directory, "Images"));
        await File.WriteAllBytesAsync(Path.Combine(_directory, "Images", "cached.png"), [1, 2, 3]);
        await File.WriteAllTextAsync(Path.Combine(_directory, "history.json"), "{broken");
        var store = Store(); await store.LoadAsync();
        Assert.NotNull(store.LoadWarning);
        Assert.Single(Directory.GetFiles(_directory, "history.json.unreadable-*"));
        Assert.Empty(store.Items);
        var imagesBackup = Assert.Single(Directory.GetDirectories(_directory, "history.json.unreadable-*.Images"));
        Assert.True(File.Exists(Path.Combine(imagesBackup, "cached.png")));
    }
    [Theory]
    [InlineData("验证码: 123456")]
    [InlineData("123456")]
    [InlineData("Bearer example-token")]
    [InlineData("api_key=private")]
    public void SensitiveTextIsExcludedFromLearning(string text) => Assert.False(new ClipRecord { Kind = ClipKind.Text, Text = text }.LearningEligible());
    [Fact]
    public void AppExclusionsUseExactExecutableNames()
    {
        var settings = new AppSettings { ExcludedApps = " KeePass.exe\nBitwarden.exe " };
        Assert.True(settings.IsExcluded("keepass"));
        Assert.False(settings.IsExcluded("keepass-test.exe"));
    }
}
