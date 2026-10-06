using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClipHarbor.Sync;

public static class Protocol
{
    public const int Version = 1;
    public const int PartBytes = 4 * 1024 * 1024;
    public const long MaxFileBytes = 100L * 1024 * 1024;
    public const long MaxBatchBytes = 500L * 1024 * 1024;
    public const long MaxImageBytes = 20L * 1024 * 1024;
    public const long QuotaBytes = 2L * 1024 * 1024 * 1024;
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };
    public static string Encode<T>(T value) => JsonSerializer.Serialize(value, Json);
    public static T Decode<T>(string json) => JsonSerializer.Deserialize<T>(json, Json) ?? throw new InvalidDataException("服务器返回了空数据。");
    public static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    public static bool IsHash(string? hash) => hash is { Length: 64 } && hash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    public static string Now => DateTimeOffset.UtcNow.ToString("O");

    public static Uri Server(string input, bool allowLocalHttp = false)
    {
        if (!Uri.TryCreate(input.Trim().TrimEnd('/'), UriKind.Absolute, out var uri) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) ||
            (uri.Scheme != "https" && !(allowLocalHttp && uri.Scheme == "http" && uri.IsLoopback)))
            throw new InvalidDataException("请填写有效 HTTPS 服务器地址。HTTP 仅允许显式启用的本机开发连接。");
        return new Uri(uri.AbsoluteUri.TrimEnd('/') + "/");
    }

    public static string SafeName(string name, ISet<string> used)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.IndexOfAny(['/', '\\', '\0']) >= 0 || Encoding.UTF8.GetByteCount(name) > 1024)
            throw new InvalidDataException("文件清单包含不安全的名称。");
        var safe = new string(name.Normalize(NormalizationForm.FormC).Select(c => c < 32 || "<>:\"|?*".Contains(c) ? '_' : c).ToArray()).TrimEnd(' ', '.');
        if (safe.Length == 0) safe = "file";
        var stem = Path.GetFileNameWithoutExtension(safe);
        var ext = Path.GetExtension(safe);
        var reserved = new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "COM¹", "COM²", "COM³", "LPT¹", "LPT²", "LPT³" };
        if (reserved.Contains(stem, StringComparer.OrdinalIgnoreCase)) stem = "_" + stem;
        static string Truncate(string value, int units, int bytes)
        {
            var result = new StringBuilder(); var size = 0;
            foreach (var rune in value.EnumerateRunes())
            {
                if (result.Length + rune.Utf16SequenceLength > units || size + rune.Utf8SequenceLength > bytes) break;
                result.Append(rune.ToString()); size += rune.Utf8SequenceLength;
            }
            return result.ToString();
        }
        ext = Truncate(ext, 24, 60); stem = Truncate(stem, 100, 160);
        safe = stem + ext;
        for (var i = 2; !used.Add(safe); i++) safe = $"{stem} ({i}){ext}";
        return safe;
    }
}

public sealed record WireFile(string Name, long ByteLength, string Sha256, string BlobId);
public sealed class WireRecord
{
    public int SchemaVersion { get; set; } = Protocol.Version;
    public string RecordId { get; set; } = Guid.NewGuid().ToString();
    public string Kind { get; set; } = "text";
    public string? Text { get; set; }
    public string? RtfBase64 { get; set; }
    public string? Html { get; set; }
    public string? BlobId { get; set; }
    public string? BlobSha256 { get; set; }
    public List<WireFile> Files { get; set; } = [];
    public string ContentHash { get; set; } = "";
    public string CapturedAt { get; set; } = Protocol.Now;
    public string LastCapturedAt { get; set; } = Protocol.Now;
    public string OriginDeviceId { get; set; } = "";
    public bool Favorite { get; set; }
    public string Note { get; set; } = "";
    public long FavoriteRevision { get; set; }
    public long NoteRevision { get; set; }
    public long Revision { get; set; }
    public string? DeletedAt { get; set; }

    // Fixed length-prefixed framing, independent of Swift/.NET JSON serializers.
    public string Digest()
    {
        using var stream = new MemoryStream();
        void Field(byte[]? bytes)
        {
            Span<byte> length = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(length, bytes?.Length ?? -1);
            stream.Write(length);
            if (bytes is not null) stream.Write(bytes);
        }
        void TextField(string? text) => Field(text is null ? null : Encoding.UTF8.GetBytes(text.Replace("\r\n", "\n").Replace('\r', '\n')));
        TextField("ClipHarbor.Sync.v1"); TextField(Kind); TextField(Text);
        Field(RtfBase64 is null ? null : Convert.FromBase64String(RtfBase64)); TextField(Html); TextField(BlobSha256);
        foreach (var file in Files) { TextField(file.Name); TextField(file.ByteLength.ToString(System.Globalization.CultureInfo.InvariantCulture)); TextField(file.Sha256); }
        return Protocol.Hash(stream.ToArray());
    }

    public void Validate()
    {
        if (Files is null || Note is null || Files.Any(f => f is null)) throw new InvalidDataException("记录缺少有效字段。");
        if (SchemaVersion != Protocol.Version || !Guid.TryParse(RecordId, out _) || Kind is not ("text" or "link" or "image" or "files"))
            throw new InvalidDataException("记录格式不兼容。");
        if (Encoding.UTF8.GetByteCount(Text ?? "") + Encoding.UTF8.GetByteCount(Html ?? "") + (RtfBase64?.Length ?? 0) > 2 * 1024 * 1024 || Note.Length > 100_000)
            throw new InvalidDataException("文本或备注超过大小限制。");
        if ((Kind is "text" or "link") && Text is null) throw new InvalidDataException("文字记录缺少正文。");
        if ((Kind != "files" && Files.Count != 0) || (Kind != "image" && (BlobId is not null || BlobSha256 is not null)) || ((Kind is "image" or "files") && (Text is not null || RtfBase64 is not null || Html is not null))) throw new InvalidDataException("记录内容类型不一致。");
        if (Kind == "image" && (!Guid.TryParse(BlobId, out _) || !Protocol.IsHash(BlobSha256 ?? ""))) throw new InvalidDataException("图片附件无效。");
        if (Kind == "files")
        {
            if (Files.Count is < 1 or > 100) throw new InvalidDataException("文件数量超过限制。");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in Files)
            {
                _ = Protocol.SafeName(file.Name, names);
                if (file.ByteLength < 0 || file.ByteLength > Protocol.MaxFileBytes || !Guid.TryParse(file.BlobId, out _) || !Protocol.IsHash(file.Sha256)) throw new InvalidDataException("文件附件无效或超过限制。");
            }
            if (Files.Sum(f => f.ByteLength) > Protocol.MaxBatchBytes) throw new InvalidDataException("文件总大小超过限制。");
        }
        if (!DateTimeOffset.TryParse(CapturedAt, out _) || !DateTimeOffset.TryParse(LastCapturedAt, out _) || ContentHash != Digest()) throw new InvalidDataException("内容校验失败。");
    }
}

public sealed record ServerMeta(string Product, string InstanceId, int ProtocolVersion, int PartBytes, long MaxFileBytes, long MaxImageBytes, long MaxBatchBytes, long QuotaBytes);
public sealed record LoginRequest(string Username, string Password, string DeviceName, string DeviceId);
public sealed record RefreshRequest(string RefreshToken);
public sealed record SessionTokens(string AccountId, string Username, string DeviceId, string SessionId, string AccessToken, string RefreshToken, string AccessExpiresAt, string RefreshExpiresAt, string RevokeToken, string SyncEpoch);
public sealed record LogoutRequest(string? RevokeToken = null);
public sealed record SyncOperation(string OperationId, string Type, string RecordId, WireRecord? Record = null, bool? Favorite = null, string? Note = null, long? ExpectedRevision = null);
public sealed record OperationsRequest(string SyncEpoch, List<SyncOperation> Operations);
public sealed record OperationResult(string OperationId, string Status, WireRecord? Record = null, string? Code = null);
public sealed record OperationsResponse(List<OperationResult> Results, string HighWater);
public sealed record ChangesResponse(List<WireRecord> Records, string Cursor, string HighWater, bool HasMore, string SyncEpoch);
public sealed record SnapshotResponse(List<WireRecord> Records, string? NextPageToken, string Cursor, string SyncEpoch);
public sealed record ClipboardPublish(string ClipboardEventId, string RecordId, string SyncEpoch);
public sealed record ClipboardEvent(string ClipboardEventId, string ClipboardSequence, string OriginDeviceId, string RecordId, string Kind, string CommittedAt);
public sealed record ClipboardLatest(string ClipboardSequence, ClipboardEvent? Event, WireRecord? Record, string SyncEpoch);
public sealed record EventNotice(string Type, string ClipboardSequence, string SyncEpoch);
public sealed record UploadRequest(string Kind, long ByteLength, string Sha256);
public sealed record UploadResponse(string UploadId, int PartBytes, List<int> ReceivedParts, string? BlobId = null);
public sealed record ApiError(string Code, string Message, string? RequestId = null);

public sealed class SyncConfig
{
    public string CredentialId { get; set; } = Guid.NewGuid().ToString();
    public string? LastSpace { get; set; }
    public string? ServerInstanceId { get; set; }
    public string ServerUrl { get; set; } = "";
    public string Username { get; set; } = "";
    public string DeviceName { get; set; } = Environment.MachineName;
    public string DeviceId { get; set; } = Guid.NewGuid().ToString();
    public bool Enabled { get; set; }
    public bool DirectPaste { get; set; } = true;
    public bool SyncText { get; set; } = true;
    public bool SyncImages { get; set; } = true;
    public bool SyncFiles { get; set; } = true;
    public bool KeepSignedIn { get; set; } = true;
    public bool AllowLocalHttp { get; set; }
    public bool Accepts(string kind) => kind switch { "image" => SyncImages, "files" => SyncFiles, _ => SyncText };
}
