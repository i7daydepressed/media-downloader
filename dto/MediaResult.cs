namespace MediaDownloader.Dto;

public sealed class MediaResult {
    public int Index { get; init; }
    public string Url { get; init; } = string.Empty;
    public bool Success { get; init; }
    public List<MediaFileResult> Files { get; init; } = new List<MediaFileResult>();
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
}
