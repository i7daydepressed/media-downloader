using System.Globalization;
using MediaDownloader.Configuration;
using MediaDownloader.Exceptions;
using Microsoft.Extensions.Options;

namespace MediaDownloader.Tools;

// служба скачивания через yt-dlp: на вход URL и папка, на выход пути готовых файлов
public sealed class YtDlpClient {
    private readonly ExternalProcessRunner runner; // запуск CLI с отменой и сроком
    private readonly FfmpegClient ffmpeg; // путь FFmpeg для объединения дорожек
    private readonly ToolOptions tools; // пути утилит и JS-среды
    private readonly MediaOptions media; // ограничение размера файла

    // конструктор: сохраняем службы запуска и настройки Tools и Media
    public YtDlpClient(ExternalProcessRunner runner, FfmpegClient ffmpeg,
        IOptions<ToolOptions> tools, IOptions<MediaOptions> media) {
        this.runner = runner;
        this.ffmpeg = ffmpeg;
        this.tools = tools.Value;
        this.media = media.Value;
    }

    // передаем скачивание основному методу без предпочтения совместимого видео
    public Task<List<string>> downloadAsync(string url, string outputDirectory, CancellationToken cancellationToken) {
        return downloadAsync(url, outputDirectory, false, cancellationToken);
    }

    // скачиваем URL в outputDirectory с выбранным предпочтением формата и возвращаем пути
    public async Task<List<string>> downloadAsync(string url, string outputDirectory, bool preferCompatibleVideo,
        CancellationToken cancellationToken) {
        // при включенном предпочтении сначала выбираем MP4/H.264 и AAC, затем запасные варианты источника
        string format = preferCompatibleVideo
            ? "bestvideo[ext=mp4][vcodec~='^(avc1|h264)']+bestaudio[acodec~='^(mp4a|aac)']/" +
                "best[ext=mp4][vcodec~='^(avc1|h264)'][acodec~='^(mp4a|aac)']/bestvideo+bestaudio/best/" +
                "bestvideo[ext=mp4][vcodec~='^(avc1|h264)']"
            : "bestvideo+bestaudio/best";
        // передаем yt-dlp папку, лимит размера и среду JS, отключаем плейлисты и пользовательский конфиг
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
        // пути берутся из after_move:filepath; усеченный список отклоняем, полный разбираем по строкам
        if (result.OutputTruncated) {
            throw new ExternalToolException("Список итоговых файлов инструмента слишком велик.", "invalid_tool_output");
        }
        return result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }

    // ищем исполняемый файл JS-среды, при недоступности передаем настроенное значение самому yt-dlp
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
