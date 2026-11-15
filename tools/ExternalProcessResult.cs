namespace MediaDownloader.Tools;

// результат внешнего процесса: код завершения и ограниченные тексты двух потоков
public sealed class ExternalProcessResult {
    public int ExitCode { get; init; } // код завершения процесса
    public string StandardOutput { get; init; } = string.Empty; // сохраненный stdout
    public string StandardError { get; init; } = string.Empty; // сохраненный stderr
    public bool OutputTruncated { get; init; } // stdout сохранился не полностью
    public bool ErrorTruncated { get; init; } // stderr сохранился не полностью
}
