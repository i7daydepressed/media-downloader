using System.Text.Json.Serialization;

namespace MediaDownloader.Dto;

public sealed class MediaFileResult {
    public int Index { get; init; }
    public string FileName { get; init; } = string.Empty;
    public long SizeBytes { get; init; }
    public double? DurationSeconds { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }

    [JsonIgnore]
    public string? FilePath { get; init; }

    [JsonIgnore]
    public bool IsCompatibleVideo { get; init; }
}
