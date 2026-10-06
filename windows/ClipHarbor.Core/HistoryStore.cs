using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClipHarbor.Core;

public sealed class HistoryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    private readonly SemaphoreSlim _saveLock = new(1);
    public string DirectoryPath { get; }
    public string ImageDirectory => Path.Combine(DirectoryPath, "Images");
    public List<ClipRecord> Items { get; private set; } = [];
    public AppSettings Settings { get; private set; } = new();
    public bool Paused { get; set; }
    public string? ActiveSyncSpace { get; set; }
    public event Action? Changed;
    public event Action<ClipRecord, string>? SyncMutation;
    public string? LoadWarning { get; private set; }

    public HistoryStore(string directoryPath) => DirectoryPath = directoryPath;

    public async Task LoadAsync()
    {
        Directory.CreateDirectory(ImageDirectory);
        Settings = await ReadAsync<AppSettings>("settings.json") ?? new();
        ActiveSyncSpace = Settings.Sync.LastSpace;
        Items = await ReadAsync<List<ClipRecord>>("history.json") ?? [];
        Prune();
    }
    private async Task<T?> ReadAsync<T>(string name)
    {
        var path = Path.Combine(DirectoryPath, name);
        if (!File.Exists(path)) return default;
        try { return JsonSerializer.Deserialize<T>(await File.ReadAllTextAsync(path), JsonOptions) ?? throw new JsonException("本地数据为空。"); }
        catch (JsonException)
        {
            var backup = path + ".unreadable-" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            File.Move(path, backup);
            if (name == "history.json" && Directory.Exists(ImageDirectory))
            {
                Directory.Move(ImageDirectory, backup + ".Images");
                Directory.CreateDirectory(ImageDirectory);
            }
            LoadWarning = "部分本地数据无法读取，原文件已保留为 .unreadable 备份，可在数据目录查看。";
            return default;
        }
    }
    public async Task SaveAsync()
    {
        // Snapshot before yielding: callers update records on the UI thread.
        var history = JsonSerializer.Serialize(Items, JsonOptions);
        var settings = JsonSerializer.Serialize(Settings, JsonOptions);
        await _saveLock.WaitAsync();
        try
        {
            await WriteAtomicAsync("history.json", history);
            await WriteAtomicAsync("settings.json", settings);
        }
        finally { _saveLock.Release(); }
    }
    private async Task WriteAtomicAsync(string name, string json)
    {
        var path = Path.Combine(DirectoryPath, name);
        await File.WriteAllTextAsync(path + ".tmp", json);
        File.Move(path + ".tmp", path, true);
    }
    public async Task<ClipRecord> AddAsync(ClipRecord record)
    {
        var key = record.ContentKey;
        var previous = Items.FirstOrDefault(i => i.ContentKey == key && (i.SyncSpace is null || i.SyncSpace == ActiveSyncSpace));
        if (previous is not null)
        {
            Items.Remove(previous);
            previous.CapturedAt = record.CapturedAt;
            previous.Source = record.Source;
            previous.ScreenshotPath = record.ScreenshotPath ?? previous.ScreenshotPath;
            if (Settings.LearningEnabled) previous.CaptureCount++;
            record = previous;
        }
        Items.Insert(0, record);
        Prune();
        Changed?.Invoke();
        await SaveAsync();
        return record;
    }
    public void Prune(DateTimeOffset? at = null)
    {
        var beforePrune = Items.ToList();
        var now = at ?? DateTimeOffset.UtcNow;
        var count = 0;
        Items = Items.Where(item =>
        {
            if (item.SyncSpace is not null && item.SyncSpace != ActiveSyncSpace) return true;
            if (item.Favorite && Settings.FavoritesExempt) return true;
            if (Settings.Lifetime(item.DisplayKind) is { } duration && now - item.CapturedAt >= duration) return false;
            return ++count <= Math.Clamp(Settings.HistoryLimit, 1, 10000);
        }).ToList();
        foreach (var removed in beforePrune.Where(item => !Items.Contains(item))) SyncMutation?.Invoke(removed, "hide");
        CleanupImages();
    }
    public async Task NotifyAsync() { Changed?.Invoke(); await SaveAsync(); }
    public async Task UpdateSettingsAsync(AppSettings settings) { Settings = settings; Prune(); await NotifyAsync(); }
    public async Task DeleteAsync(ClipRecord item) { SyncMutation?.Invoke(item, "delete"); Items.Remove(item); CleanupImages(); await NotifyAsync(); }
    public async Task ClearAsync(bool keepFavorites) { foreach (var item in Items.Where(i => (!keepFavorites || !i.Favorite) && (i.SyncSpace is null || i.SyncSpace == ActiveSyncSpace)).ToList()) { SyncMutation?.Invoke(item, "hide"); Items.Remove(item); } CleanupImages(); await NotifyAsync(); }
    public async Task RecordUseAsync(ClipRecord item)
    {
        if (!Settings.LearningEnabled) return;
        item.UseCount++; item.LastUsedAt = DateTimeOffset.UtcNow;
        await NotifyAsync();
    }
    public async Task ImportRemoteAsync(ClipRecord record)
    {
        var existing = Items.FirstOrDefault(i => i.SyncSpace == record.SyncSpace && (i.SyncRecordId == record.SyncRecordId || i.SyncContentHash == record.SyncContentHash));
        if (existing is not null)
        {
            record.Id = existing.Id; record.CaptureCount = existing.CaptureCount; record.UseCount = existing.UseCount; record.LastUsedAt = existing.LastUsedAt; record.ExcludedFromLearning = existing.ExcludedFromLearning;
            record.ScreenshotPath = existing.ScreenshotPath;
            Items.Remove(existing);
        }
        Items.Insert(0, record); Items.Sort((a, b) => b.CapturedAt.CompareTo(a.CapturedAt)); Prune(); await NotifyAsync();
    }
    public IEnumerable<ClipRecord> Frequent() => !Settings.LearningEnabled ? [] : Items.Where(i => i.LearningEligible() && i.CaptureCount + i.UseCount >= Math.Max(2, Settings.LearningThreshold)).OrderByDescending(i => i.LearningScore).ThenByDescending(i => i.LastUsedAt ?? i.CapturedAt);
    public string ImagePath(string name)
    {
        if (string.IsNullOrEmpty(name) || Path.GetFileName(name) != name || name.Contains('\\')) throw new InvalidDataException("图片缓存名称无效。");
        return Path.Combine(ImageDirectory, name);
    }
    public async Task<string> CacheImageAsync(byte[] png)
    {
        if (png.Length > Math.Clamp(Settings.ImageLimitMB, 1, 100) * 1024 * 1024) throw new InvalidDataException("图片超过设置的大小限制，未记录。");
        var name = Convert.ToHexString(SHA256.HashData(png)).ToLowerInvariant() + ".png";
        if (!File.Exists(ImagePath(name))) await File.WriteAllBytesAsync(ImagePath(name), png);
        return name;
    }
    private void CleanupImages()
    {
        if (!Directory.Exists(ImageDirectory)) return;
        var kept = Items.Where(i => i.ImageName is not null).Select(i => i.ImageName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(ImageDirectory, "*.png"))
            if (!kept.Contains(Path.GetFileName(path))) { try { File.Delete(path); } catch (IOException) { /* A preview may still hold the file open. */ } }
    }
}
