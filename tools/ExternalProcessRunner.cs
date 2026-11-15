using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using MediaDownloader.Configuration;
using MediaDownloader.Exceptions;
using Microsoft.Extensions.Options;

namespace MediaDownloader.Tools;

public sealed class ExternalProcessRunner {
    private readonly ToolOptions options;
    private readonly string contentRoot;
    private readonly ILogger<ExternalProcessRunner> logger;
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(5);

    public ExternalProcessRunner(IOptions<ToolOptions> options, IHostEnvironment environment,
        ILogger<ExternalProcessRunner> logger) {
        this.options = options.Value;
        contentRoot = environment.ContentRootPath;
        this.logger = logger;
    }

    public string resolveExecutablePath(string configuredPath) {
        if (Path.IsPathRooted(configuredPath) || configuredPath.Contains(Path.DirectorySeparatorChar) ||
            configuredPath.Contains(Path.AltDirectorySeparatorChar)) {
            string fullPath = Path.GetFullPath(configuredPath, contentRoot);
            if (isExecutable(fullPath)) {
                return fullPath;
            }
        }
        else {
            string[] names = OperatingSystem.IsWindows() && string.IsNullOrEmpty(Path.GetExtension(configuredPath))
                ? new[] { configuredPath + ".exe", configuredPath + ".com" }
                : new[] { configuredPath };
            foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator)) {
                if (string.IsNullOrWhiteSpace(directory)) {
                    continue;
                }
                foreach (string name in names) {
                    string candidate = Path.GetFullPath(Path.Combine(directory.Trim('"'), name), contentRoot);
                    if (isExecutable(candidate)) {
                        return candidate;
                    }
                }
            }
        }
        throw new ExternalToolException("Не найден необходимый внешний инструмент.", "tool_unavailable");
    }

    public async Task<ExternalProcessResult> runAsync(string executablePath, IReadOnlyList<string> arguments,
        string workingDirectory, CancellationToken cancellationToken) {
        cancellationToken.ThrowIfCancellationRequested();
        string executable = resolveExecutablePath(executablePath);
        if (!Directory.Exists(workingDirectory)) {
            throw new DirectoryNotFoundException("Рабочий каталог процесса отсутствует.");
        }
        ProcessStartInfo startInfo = new ProcessStartInfo {
            FileName = executable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true
        };
        foreach (string argument in arguments) {
            startInfo.ArgumentList.Add(argument);
        }
        using Process process = new Process { StartInfo = startInfo };
        try {
            process.Start();
        }
        catch (Exception exception) when (exception is Win32Exception or FileNotFoundException) {
            throw new ExternalToolException("Не удалось запустить внешний инструмент.", "tool_unavailable");
        }
        process.StandardInput.Close();
        using CancellationTokenSource captureCancellation = new CancellationTokenSource();
        Task<CapturedOutput> stdout = readBoundedAsync(process.StandardOutput, captureCancellation.Token);
        Task<CapturedOutput> stderr = readBoundedAsync(process.StandardError, captureCancellation.Token);
        using CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(options.ProcessTimeoutSeconds));
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try {
            await process.WaitForExitAsync(linked.Token);
            await Task.WhenAll(stdout, stderr).WaitAsync(DrainTimeout, linked.Token);
        }
        catch (OperationCanceledException) {
            await terminateAsync(process, captureCancellation, stdout, stderr);
            cancellationToken.ThrowIfCancellationRequested();
            throw new ExternalToolException("Превышено время работы внешнего инструмента.", "tool_timeout");
        }
        catch (TimeoutException) {
            await terminateAsync(process, captureCancellation, stdout, stderr);
            throw new ExternalToolException("Не удалось завершить чтение вывода инструмента.", "tool_output_timeout");
        }
        catch {
            await terminateAsync(process, captureCancellation, stdout, stderr);
            throw;
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (process.ExitCode != 0) {
            throw new ExternalToolException("Внешний инструмент не смог выполнить обработку.", "tool_failed");
        }
        return new ExternalProcessResult {
            ExitCode = process.ExitCode,
            StandardOutput = stdout.Result.Text,
            StandardError = stderr.Result.Text,
            OutputTruncated = stdout.Result.Truncated,
            ErrorTruncated = stderr.Result.Truncated
        };
    }

    private async Task<CapturedOutput> readBoundedAsync(StreamReader reader, CancellationToken cancellationToken) {
        char[] buffer = new char[4096];
        StringBuilder text = new StringBuilder(Math.Min(options.MaxCapturedOutputCharacters, 4096));
        bool truncated = false;
        try {
            int count;
            while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0) {
                int retained = Math.Min(count, options.MaxCapturedOutputCharacters - text.Length);
                text.Append(buffer, 0, retained);
                truncated |= retained < count;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
            truncated = true;
        }
        return new CapturedOutput { Text = text.ToString(), Truncated = truncated };
    }

    private async Task terminateAsync(Process process, CancellationTokenSource captureCancellation,
        Task<CapturedOutput> stdout, Task<CapturedOutput> stderr) {
        try {
            if (!process.HasExited) {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or NotSupportedException) {
            logger.LogWarning("Process tree termination returned {ExceptionType}.", exception.GetType().Name);
        }
        try {
            await process.WaitForExitAsync().WaitAsync(DrainTimeout);
        }
        catch (Exception exception) when (exception is TimeoutException or InvalidOperationException) {
            logger.LogWarning("Process exit wait returned {ExceptionType}.", exception.GetType().Name);
        }
        captureCancellation.Cancel();
        try {
            await Task.WhenAll(stdout, stderr).WaitAsync(DrainTimeout);
        }
        catch (Exception exception) when (exception is TimeoutException or IOException or ObjectDisposedException) {
            logger.LogWarning("Process output drain returned {ExceptionType}.", exception.GetType().Name);
        }
    }

    private static bool isExecutable(string path) {
        if (!File.Exists(path) || Directory.Exists(path)) {
            return false;
        }
        if (OperatingSystem.IsWindows()) {
            return Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
                Path.GetExtension(path).Equals(".com", StringComparison.OrdinalIgnoreCase);
        }
        UnixFileMode mode = File.GetUnixFileMode(path);
        return (mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
    }

    private sealed class CapturedOutput {
        public string Text { get; init; } = string.Empty;
        public bool Truncated { get; init; }
    }
}
