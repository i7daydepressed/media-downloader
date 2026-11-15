namespace MediaDownloader.Configuration;

public sealed class MediaOptions {
    public const string SectionName = "Media";

    public int MaxUrlsPerBatch { get; set; } = 10;
    public int MaxFilesPerUrl { get; set; } = 100;
    public int MaxConcurrentOperations { get; set; } = 1;
    public string WorkingRoot { get; set; } = "data/work";
    public long MaxFileSizeBytes { get; set; } = 2_000_000_000;
    public bool AllowLocalUrls { get; set; }
}
