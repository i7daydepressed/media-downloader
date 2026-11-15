namespace MediaDownloader.Configuration;

public sealed class ToolOptions {
    public const string SectionName = "Tools";

    public string YtDlpPath { get; set; } = "yt-dlp";
    public string GalleryDlPath { get; set; } = "gallery-dl";
    public string FfmpegPath { get; set; } = "ffmpeg";
    public string FfprobePath { get; set; } = "ffprobe";
    public string JavaScriptRuntime { get; set; } = "deno";
    public string JavaScriptRuntimePath { get; set; } = "deno";
    public int ProcessTimeoutSeconds { get; set; } = 600;
    public int MaxCapturedOutputCharacters { get; set; } = 65_536;
}
