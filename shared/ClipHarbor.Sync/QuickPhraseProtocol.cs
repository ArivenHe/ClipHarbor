using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ClipHarbor.Sync;

public sealed class PhraseEntity
{
    public int SchemaVersion { get; set; } = 1;
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Kind { get; set; } = "phrase";
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public string? Alias { get; set; }
    public string? GroupId { get; set; }
    public bool Pinned { get; set; }
    public bool Hidden { get; set; }
    public string? OriginPresetId { get; set; }
    public int? OriginPresetVersion { get; set; }
    public string CreatedAt { get; set; } = Protocol.Now;
    public string UpdatedAt { get; set; } = Protocol.Now;
    public string Revision { get; set; } = "0";
    public string? DeletedAt { get; set; }
    [JsonIgnore] public long Version => long.Parse(Revision, System.Globalization.CultureInfo.InvariantCulture);
    public PhraseEntity Copy() => Protocol.Decode<PhraseEntity>(Protocol.Encode(this));
    public void Validate()
    {
        if (SchemaVersion != 1 || Title is null || Body is null || !long.TryParse(Revision, out var revision) || revision < 0)
            throw new InvalidDataException("短语格式不兼容。");
        if (Kind == "preference")
        {
            if (!PresetIdValid(Id)) throw new InvalidDataException("预置标识无效。");
            Title = Body = ""; Alias = GroupId = OriginPresetId = null; OriginPresetVersion = null;
            return;
        }
        if (Kind is not ("phrase" or "group") || !Guid.TryParse(Id, out _)) throw new InvalidDataException("短语 ID 或类型无效。");
        Title = Title.Trim();
        if (Title.Length == 0 || Title.EnumerateRunes().Count() > (Kind == "group" ? 40 : 80) || Title.Any(char.IsControl))
            throw new InvalidDataException(Kind == "group" ? "分组名称需为 1–40 个字符。" : "标题需为 1–80 个字符。");
        if (Kind == "group") { Body = ""; Alias = GroupId = OriginPresetId = null; OriginPresetVersion = null; return; }
        Body = Body.Replace("\r\n", "\n").Replace('\r', '\n');
        if (string.IsNullOrWhiteSpace(Body) || Encoding.UTF8.GetByteCount(Body) > 64 * 1024) throw new InvalidDataException("正文不能为空，且不能超过 64 KiB。");
        Alias = string.IsNullOrWhiteSpace(Alias) ? null : Alias.Trim().ToLowerInvariant();
        if (Alias is not null && !Regex.IsMatch(Alias, "^[a-z0-9_-]{1,32}$")) throw new InvalidDataException("别名需为 1–32 个字母、数字、下划线或短横线。");
        if (GroupId is not null && !Guid.TryParse(GroupId, out _)) throw new InvalidDataException("分组 ID 无效。");
        if ((OriginPresetId is null) != (OriginPresetVersion is null) || (OriginPresetId is not null && (!PresetIdValid(OriginPresetId) || OriginPresetVersion < 1)))
            throw new InvalidDataException("预置来源无效。");
    }
    public static bool PresetIdValid(string? id) => id is not null && Regex.IsMatch(id, "^[a-z0-9][a-z0-9._-]{0,99}$");
}

public sealed record PhraseOperation(string OperationId, string Type, PhraseEntity Entity, string ExpectedRevision = "0", PhraseEntity? NewGroup = null);
public sealed record PhraseOperationsRequest(string SyncEpoch, List<PhraseOperation> Operations);
public sealed record PhraseResult(string OperationId, string Status, PhraseEntity? Entity = null, string? Code = null, List<PhraseEntity>? Related = null);
public sealed record PhraseOperationsResponse(List<PhraseResult> Results, string HighWater);
public sealed record PhraseChanges(List<PhraseEntity> Entities, string Cursor, string HighWater, bool HasMore, string SyncEpoch);
public sealed record PhraseSnapshot(string Token, string Cursor, string SyncEpoch);
public sealed record PhraseSnapshotPage(List<PhraseEntity> Entities, bool HasMore, string Cursor, string SyncEpoch);
public sealed record PhraseFailure(string Id, PhraseOperation Operation, string Reason, PhraseEntity? Server);
public sealed record PhraseLibrary(List<PhraseEntity> Entities, List<PhraseFailure> Failures, List<string> PendingIds, string Status = "", bool Supported = true);
public sealed record PhraseLimits(int MaxPhrases = 2000, int MaxGroups = 100, int MaxBodyBytes = 65536);
public sealed record SyncFeatures(int QuickPhrases = 1);

public sealed class PresetCatalog
{
    public int SchemaVersion { get; set; } = 1;
    public int CatalogVersion { get; set; } = 1;
    public string Locale { get; set; } = "zh-CN";
    public List<PresetPhrase> Phrases { get; set; } = [];
    public void Validate()
    {
        if (SchemaVersion != 1 || CatalogVersion < 1 || Phrases is null || Phrases.Count > 2000) throw new InvalidDataException("预置目录无效。");
        var ids = new HashSet<string>();
        foreach (var phrase in Phrases)
        {
            if (!ids.Add(phrase.PresetId) || !PhraseEntity.PresetIdValid(phrase.PresetId) || phrase.PresetVersion < 1 || !PhraseEntity.PresetIdValid(phrase.CategoryKey) || string.IsNullOrWhiteSpace(phrase.CategoryName)) throw new InvalidDataException("预置条目无效。");
            new PhraseEntity { Title = phrase.Title, Body = phrase.Body, Alias = phrase.Alias }.Validate();
        }
    }
    public static PresetCatalog Load()
    {
        using var stream = typeof(PresetCatalog).Assembly.GetManifestResourceStream("ClipHarbor.quick-phrases.zh-CN.json") ?? throw new InvalidDataException("预置资源缺失。");
        using var reader = new StreamReader(stream); var catalog = Protocol.Decode<PresetCatalog>(reader.ReadToEnd()); catalog.Validate(); return catalog;
    }
}
public sealed record PresetPhrase(string PresetId, int PresetVersion, string Title, string Body, string? Alias, string CategoryKey, string CategoryName);
