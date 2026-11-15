using MediaDownloader.Configuration;
using Microsoft.Extensions.Options;

namespace MediaDownloader.Tools;

public sealed class FfmpegClient {
    private readonly ExternalProcessRunner runner;
    private readonly ToolOptions options;
    private readonly string contentRoot;

    public FfmpegClient(ExternalProcessRunner runner, IOptions<ToolOptions> options, IHostEnvironment environment) {
        this.runner = runner;
        this.options = options.Value;
        contentRoot = environment.ContentRootPath;
    }

    public string resolveExecutablePath() {
        string configuredPath = Path.GetFullPath(options.FfmpegPath, contentRoot);
        if (Directory.Exists(configuredPath)) {
            return runner.resolveExecutablePath(Path.Combine(configuredPath, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg"));
        }
        return runner.resolveExecutablePath(options.FfmpegPath);
    }

    public async Task<string> getVersionAsync(CancellationToken cancellationToken) {
        ExternalProcessResult result = await runner.runAsync(resolveExecutablePath(), new[] { "-version" }, contentRoot, cancellationToken);
        return result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? string.Empty;
    }

    public async Task remuxToMp4Async(string sourcePath, string outputPath, CancellationToken cancellationToken) {
        await runner.runAsync(resolveExecutablePath(), new[] {
            "-hide_banner", "-loglevel", "error", "-nostdin", "-n", "-i", sourcePath,
            "-map", "0:v:0", "-map", "0:a:0?", "-c", "copy", "-movflags", "+faststart", "-f", "mp4", outputPath
        }, Path.GetDirectoryName(outputPath)!, cancellationToken);
    }
}
