namespace MediaDownloader.Dto;

public sealed class ServiceErrorResponse {
    public string Code { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public int Status { get; init; }
    public string TraceId { get; init; } = string.Empty;
}
