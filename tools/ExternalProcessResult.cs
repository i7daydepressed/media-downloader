namespace MediaDownloader.Tools;

public sealed class ExternalProcessResult {
    public int ExitCode { get; init; }
    public string StandardOutput { get; init; } = string.Empty;
    public string StandardError { get; init; } = string.Empty;
    public bool OutputTruncated { get; init; }
    public bool ErrorTruncated { get; init; }
}
