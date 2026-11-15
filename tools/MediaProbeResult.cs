namespace MediaDownloader.Tools;

public sealed class MediaProbeResult {
    public double? DurationSeconds { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public string? VideoCodec { get; init; }
    public string? AudioCodec { get; init; }
    public string? PixelFormat { get; init; }
    public string? ContainerFormat { get; init; }
    public bool IsCompatibleVideo { get; init; }
}
