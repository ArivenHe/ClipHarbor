using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ClipHarbor.Core;

public enum ClipKind { Text, Link, Image, Files }
public enum FileCategory { Document, Spreadsheet, Presentation, Image, Audio, Video, Archive, Code, Folder, Other }

public sealed class ClipRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset CapturedAt { get; set; } = DateTimeOffset.UtcNow;
    public ClipKind Kind { get; set; }
    public string? Text { get; set; }
    public string? RichText { get; set; }
    public string? Html { get; set; }
    public string? ImageName { get; set; }
    public List<string> FilePaths { get; set; } = [];
    public string Source { get; set; } = "";
    public bool Favorite { get; set; }
    public string Note { get; set; } = "";
    public string? ScreenshotPath { get; set; }
    public int CaptureCount { get; set; } = 1;
    public int UseCount { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
    public bool ExcludedFromLearning { get; set; }

    // Presentation and clipboard transport are separate: PNG references are
    // images in the UI, and are still copied back as the original files.
    [JsonIgnore] public ClipKind DisplayKind => Kind == ClipKind.Files && FilePaths.Count > 0 && FilePaths.All(p => FileTypes.Classify(p) == FileCategory.Image) ? ClipKind.Image : Kind;
    [JsonIgnore] public string KindTitle => DisplayKind switch { ClipKind.Text => "文本", ClipKind.Link => "链接", ClipKind.Image => "图片", _ => "文件" };
    [JsonIgnore] public string Glyph => DisplayKind switch { ClipKind.Text => "\uE8A4", ClipKind.Link => "\uE71B", ClipKind.Image => "\uEB9F", _ => "\uE8A5" };
    [JsonIgnore] public string Title => Kind == ClipKind.Files ? string.Join("、", FilePaths.Select(Path.GetFileName)) : Kind == ClipKind.Image ? (ScreenshotPath is null ? "图片" : Path.GetFileName(ScreenshotPath)) : (Text ?? "")[..Math.Min(Text?.Length ?? 0, 240)];
    [JsonIgnore] public string Metadata => $"{KindTitle} · {Source} · {CapturedAt.ToLocalTime():MM-dd HH:mm}";
    [JsonIgnore] public string FavoriteLabel => Favorite ? "★" : "";
    [JsonIgnore] public int LearningScore => CaptureCount + UseCount * 3;
    [JsonIgnore] public string ContentKey => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(new { Kind, Text, RichText, Html, ImageName, FilePaths }))));

    public bool Matches(string query) => string.IsNullOrWhiteSpace(query) || new[] { Title, Text ?? "", Note, Source }.Any(s => s.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase));
    public bool LearningEligible()
    {
        if (ExcludedFromLearning) return false;
        if (Text is null) return true;
        var text = Text.Trim();
        try { return !Regex.IsMatch(text, @"(?i)(password|passwd|secret|token|authorization|api[_-]?key|验证码|密码)\s*[:=]|\bsk-[A-Za-z0-9_-]{12,}|\bBearer\s+\S+|^\d{4,8}$|^[A-Za-z0-9_+/=-]{20,}$", RegexOptions.None, TimeSpan.FromMilliseconds(100)); }
        catch (RegexMatchTimeoutException) { return false; }
    }
}

public static class FileTypes
{
    public static FileCategory Classify(string path)
    {
        if (Directory.Exists(path)) return FileCategory.Folder;
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp" or ".tif" or ".tiff" or ".heic" or ".heif" or ".avif" or ".ico" or ".svg" => FileCategory.Image,
            ".pdf" or ".txt" or ".md" or ".doc" or ".docx" or ".rtf" or ".odt" => FileCategory.Document,
            ".xls" or ".xlsx" or ".csv" or ".tsv" or ".ods" => FileCategory.Spreadsheet,
            ".ppt" or ".pptx" or ".odp" => FileCategory.Presentation,
            ".mp3" or ".wav" or ".flac" or ".aac" or ".m4a" or ".ogg" => FileCategory.Audio,
            ".mp4" or ".mov" or ".mkv" or ".avi" or ".webm" or ".wmv" => FileCategory.Video,
            ".zip" or ".7z" or ".rar" or ".tar" or ".gz" => FileCategory.Archive,
            ".cs" or ".swift" or ".js" or ".ts" or ".tsx" or ".jsx" or ".py" or ".rs" or ".go" or ".c" or ".cpp" or ".h" or ".java" or ".json" or ".yaml" or ".yml" or ".ps1" or ".sh" or ".css" or ".html" or ".xml" or ".log" => FileCategory.Code,
            _ => FileCategory.Other
        };
    }
    public static string Title(FileCategory category) => category switch
    {
        FileCategory.Document => "文档", FileCategory.Spreadsheet => "表格", FileCategory.Presentation => "演示",
        FileCategory.Image => "图片", FileCategory.Audio => "音频", FileCategory.Video => "视频",
        FileCategory.Archive => "压缩包", FileCategory.Code => "代码", FileCategory.Folder => "文件夹", _ => "其他"
    };
}
