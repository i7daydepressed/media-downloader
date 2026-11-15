using System.Globalization;
using MediaDownloader.Configuration;
using MediaDownloader.Exceptions;
using Microsoft.Extensions.Options;

namespace MediaDownloader.Tools;

public sealed class YtDlpClient {
    private readonly ExternalProcessRunner runner;
    private readonly FfmpegClient ffmpeg;
    private readonly ToolOptions tools;
    private readonly MediaOptions media;

    public YtDlpClient(ExternalProcessRunner runner, FfmpegClient ffmpeg,
        IOptions<ToolOptions> tools, IOptions<MediaOptions> media) {
        this.runner = runner;
        this.ffmpeg = ffmpeg;
        this.tools = tools.Value;
        this.media = media.Value;
    }

    public Task<List<string>> downloadAsync(string url, string outputDirectory, CancellationToken cancellationToken) {
        return downloadAsync(url, outputDirectory, false, cancellationToken);
    }

    public async Task<List<string>> downloadAsync(string url, string outputDirectory, bool preferCompatibleVideo,
        CancellationToken cancellationToken) {
        string format = preferCompatibleVideo
            ? "bestvideo[ext=mp4][vcodec~='^(avc1|h264)']+bestaudio[acodec~='^(mp4a|aac)']/" +
                "best[ext=mp4][vcodec~='^(avc1|h264)'][acodec~='^(mp4a|aac)']/bestvideo+bestaudio/best/" +
                "bestvideo[ext=mp4][vcodec~='^(avc1|h264)']"
            : "bestvideo+bestaudio/best";
        ExternalProcessResult result = await runner.runAsync(tools.YtDlpPath, new[] {
            "--ignore-config", "--no-plugin-dirs", "--no-playlist", "--no-cache-dir", "--no-progress",
            "--socket-timeout", "15", "--retries", "0", "--fragment-retries", "0", "--extractor-retries", "0",
            "--abort-on-unavailable-fragments", "--flat-playlist", "--playlist-items", "1",
            "--break-match-filters", "!is_live & !is_upcoming",
            "--max-filesize", media.MaxFileSizeBytes.ToString(CultureInfo.InvariantCulture),
            "--format", format, "--merge-output-format", preferCompatibleVideo ? "mp4/mkv" : "mkv",
            "--ffmpeg-location", ffmpeg.resolveExecutablePath(), "--no-js-runtimes",
            "--js-runtimes", tools.JavaScriptRuntime + ":" + resolveRuntimePath(),
            "--paths", outputDirectory, "--output", "media.%(ext)s", "--no-simulate",
            "--print", "after_move:filepath", "--", url
        }, outputDirectory, cancellationToken);
        if (result.OutputTruncated) {
            throw new ExternalToolException("Список итоговых файлов инструмента слишком велик.", "invalid_tool_output");
        }
        return result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }

    private string resolveRuntimePath() {
        try {
            return runner.resolveExecutablePath(tools.JavaScriptRuntimePath);
        }
        catch (ExternalToolException) {
            // Для источника без JS инструмент может работать; YouTube-проверка требует установленный runtime/EJS.
            return tools.JavaScriptRuntimePath;
        }
    }
}
