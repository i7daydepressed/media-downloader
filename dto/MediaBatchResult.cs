using System.Text.Json.Serialization;

namespace MediaDownloader.Dto;

public sealed class MediaBatchResult {
    public List<MediaResult> Results { get; init; } = new List<MediaResult>();

    [JsonIgnore]
    public string? WorkingDirectory { get; init; }
}
