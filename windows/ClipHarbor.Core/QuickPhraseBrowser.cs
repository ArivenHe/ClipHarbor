using ClipHarbor.Sync;
using System.Text;

namespace ClipHarbor.Core;

public sealed record PhraseUsage(int Count, DateTimeOffset LastUsed);
public sealed record PhraseRow(PhraseEntity Entity, PresetPhrase? Preset, string Category, bool Pending = false)
{
    public string Id => (Preset is null ? "personal:" : "preset:") + Entity.Id;
    public bool Personal => Preset is null;
    public string Source => Personal ? Entity.OriginPresetId is null ? "我的短语" : "我的 · 自定义预置" : "预置短语";
    public string Metadata => $"{(Entity.Pinned ? "置顶 · " : "")}{Source} · {Category}{(Pending ? " · 待同步" : "")}{(Entity.Hidden ? " · 已隐藏" : "")}";
    public string Preview => Entity.Body.Replace('\n', ' ');
}

public static class QuickPhraseBrowser
{
    public static List<PhraseRow> Rows(PresetCatalog catalog, PhraseLibrary library, string section = "all", string category = "all", string query = "", bool showHidden = false, IReadOnlyDictionary<string, PhraseUsage>? usage = null)
    {
        var live = library.Entities.Where(e => e.DeletedAt is null).ToList();
        var groups = live.Where(e => e.Kind == "group").ToDictionary(e => e.Id, e => e.Title);
        var personal = live.Where(e => e.Kind == "phrase").ToList();
        var sources = personal.Select(e => e.OriginPresetId).ToHashSet();
        var preferences = live.Where(e => e.Kind == "preference").ToDictionary(e => e.Id);
        List<PhraseRow> rows = section == "preset" ? [] : personal.Select(e => new PhraseRow(e, null, e.GroupId is { } id && groups.TryGetValue(id, out var name) ? name : "未分组", library.PendingIds.Contains(e.Id))).ToList();
        if (section is not ("mine" or "ungrouped"))
            foreach (var preset in catalog.Phrases)
            {
                preferences.TryGetValue(preset.PresetId, out var preference);
                if (!showHidden && preference?.Hidden == true || section != "preset" && sources.Contains(preset.PresetId)) continue;
                rows.Add(new(new() { Id = preset.PresetId, Title = preset.Title, Body = preset.Body, Alias = preset.Alias, Pinned = preference?.Pinned ?? false, Hidden = preference?.Hidden ?? false }, preset, preset.CategoryName));
            }
        if (section == "pinned") rows = rows.Where(r => r.Entity.Pinned).ToList();
        if (section == "ungrouped") rows = rows.Where(r => r.Entity.GroupId is null).ToList();
        if (category != "all") rows = rows.Where(r => r.Category == category).ToList();
        static string Normalize(string value) => value.Normalize(NormalizationForm.FormC).ToLowerInvariant();
        var normalized = Normalize(query.Trim()); if (normalized.StartsWith('/')) normalized = normalized[1..];
        var words = normalized.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        rows = rows.Where(r => words.All(word => new[] { r.Entity.Title, r.Entity.Alias ?? "", r.Entity.Body, r.Category }.Any(f => Normalize(f).Contains(word, StringComparison.Ordinal)))).ToList();
        int Rank(PhraseRow row) => normalized.Length == 0 ? 2 : new[] { row.Entity.Title, row.Entity.Alias ?? "" }.Any(v => Normalize(v) == normalized) ? 0 : new[] { row.Entity.Title, row.Entity.Alias ?? "" }.Any(v => Normalize(v).StartsWith(normalized, StringComparison.Ordinal)) ? 1 : 2;
        return rows.OrderBy(Rank).ThenByDescending(r => r.Entity.Pinned).ThenByDescending(r => usage?.GetValueOrDefault(r.Id)?.LastUsed ?? DateTimeOffset.MinValue).ThenBy(r => r.Entity.Title, StringComparer.CurrentCultureIgnoreCase).ThenBy(r => r.Id, StringComparer.Ordinal).ToList();
    }
}

public sealed class PhraseLocalState
{
    private readonly string directory;
    private Dictionary<string, PhraseUsage> usage = [];
    public PhraseLocalState(string root)
    {
        directory = Path.Combine(root, "QuickPhrases"); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "usage.json");
        if (File.Exists(path)) usage = Protocol.Decode<Dictionary<string, PhraseUsage>>(File.ReadAllText(path));
    }
    public IReadOnlyDictionary<string, PhraseUsage> Usage(string? space) => usage.Where(p => p.Key.StartsWith((space ?? "public") + "|", StringComparison.Ordinal)).ToDictionary(p => p.Key[((space ?? "public").Length + 1)..], p => p.Value);
    public void Used(string? space, PhraseRow row)
    {
        var key = (space ?? "public") + "|" + row.Id; usage.TryGetValue(key, out var previous);
        usage[key] = new((previous?.Count ?? 0) + 1, DateTimeOffset.UtcNow); Write(Path.Combine(directory, "usage.json"), Protocol.Encode(usage));
    }
    private string DraftPath(string space, string id)
    {
        if (space.Length != 64 || !space.All(Uri.IsHexDigit) || !Guid.TryParse(id, out _)) throw new InvalidDataException("草稿账号或 ID 无效。");
        return Path.Combine(directory, $"draft-{space}-{id}.json");
    }
    public void Retain(string space, PhraseEntity draft, string groupName = "") { Write(DraftPath(space, draft.Id), Protocol.Encode(draft)); Write(DraftPath(space, draft.Id) + ".group", groupName); }
    public string? GroupName(string space, string id) { var path = DraftPath(space, id) + ".group"; return File.Exists(path) ? File.ReadAllText(path) : null; }
    public void Clear(string space, string id) { File.Delete(DraftPath(space, id)); File.Delete(DraftPath(space, id) + ".group"); }
    public List<PhraseEntity> Drafts(string space) => Directory.EnumerateFiles(directory, $"draft-{space}-*.json").Select(path => Protocol.Decode<PhraseEntity>(File.ReadAllText(path))).ToList();
    private static void Write(string path, string json) { var temp = path + ".tmp"; File.WriteAllText(temp, json, new UTF8Encoding(false)); File.Move(temp, path, true); }
}
