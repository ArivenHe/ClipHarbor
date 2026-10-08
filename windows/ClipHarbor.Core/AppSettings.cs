namespace ClipHarbor.Core;

public sealed class RetentionRule
{
    public bool Override { get; set; }
    public int Value { get; set; } = 30;
    public string Unit { get; set; } = "days";
    public TimeSpan? Duration => Unit switch
    {
        "forever" => null,
        "minutes" => TimeSpan.FromMinutes(Math.Clamp(Value, 1, 100000)),
        "hours" => TimeSpan.FromHours(Math.Clamp(Value, 1, 100000)),
        "weeks" => TimeSpan.FromDays(Math.Clamp(Value, 1, 100000) * 7d),
        _ => TimeSpan.FromDays(Math.Clamp(Value, 1, 100000))
    };
}

public sealed class AppSettings
{
    public ClipHarbor.Sync.SyncConfig Sync { get; set; } = new();
    public bool RecordText { get; set; } = true;
    public bool RecordImages { get; set; } = true;
    public bool RecordFiles { get; set; } = true;
    public int HistoryLimit { get; set; } = 1000;
    public int ImageLimitMB { get; set; } = 20;
    public bool FavoritesExempt { get; set; } = true;
    public bool AutoPaste { get; set; }
    public bool PlainText { get; set; }
    public bool LearningEnabled { get; set; } = true;
    public int LearningThreshold { get; set; } = 2;
    public bool WatchScreenshots { get; set; } = true;
    public string ScreenshotFolder { get; set; } = "";
    public string ExcludedApps { get; set; } = "";
    public uint HotkeyModifiers { get; set; } = 3; // Ctrl + Alt
    public uint PhraseHotkeyModifiers { get; set; }
    public uint PhraseHotkeyKey { get; set; }
    public uint HotkeyKey { get; set; } = 0x56; // V
    public RetentionRule Retention { get; set; } = new();
    public Dictionary<ClipKind, RetentionRule> TypeRetention { get; set; } = Enum.GetValues<ClipKind>().ToDictionary(k => k, _ => new RetentionRule());
    public TimeSpan? Lifetime(ClipKind kind) => TypeRetention.TryGetValue(kind, out var rule) && rule.Override ? rule.Duration : Retention.Duration;
    public bool ShouldRecord(ClipKind kind) => kind switch { ClipKind.Image => RecordImages, ClipKind.Files => RecordFiles, _ => RecordText };
    public bool IsExcluded(string processName) => ExcludedApps.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Any(s => string.Equals(Path.GetFileNameWithoutExtension(s), Path.GetFileNameWithoutExtension(processName), StringComparison.OrdinalIgnoreCase));
}
