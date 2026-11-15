using MediaDownloader.Configuration;
using Microsoft.Extensions.Options;

namespace MediaDownloader.Tools;

// служба запуска FFmpeg: получаем версию и перепаковываем видео в MP4 копированием дорожек
public sealed class FfmpegClient {
    private readonly ExternalProcessRunner runner; // запуск процесса с ограничениями и отменой
    private readonly ToolOptions options; // путь FFmpeg из раздела Tools
    private readonly string contentRoot; // корень приложения для относительных путей

    // конструктор: сохраняем средство запуска, настройки Tools и корень приложения
    public FfmpegClient(ExternalProcessRunner runner, IOptions<ToolOptions> options, IHostEnvironment environment) {
        this.runner = runner;
        this.options = options.Value;
        contentRoot = environment.ContentRootPath;
    }

    // если настроена папка, ищем в ней ffmpeg для текущей ОС, иначе проверяем настроенное имя или путь
    public string resolveExecutablePath() {
        string configuredPath = Path.GetFullPath(options.FfmpegPath, contentRoot);
        if (Directory.Exists(configuredPath)) {
            return runner.resolveExecutablePath(Path.Combine(configuredPath, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg"));
        }
        return runner.resolveExecutablePath(options.FfmpegPath);
    }

    // запускаем FFmpeg с -version и возвращаем первую строку вывода
    public async Task<string> getVersionAsync(CancellationToken cancellationToken) {
        ExternalProcessResult result = await runner.runAsync(resolveExecutablePath(), new[] { "-version" }, contentRoot, cancellationToken);
        return result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? string.Empty;
    }

    // копируем первую видеодорожку и необязательную аудиодорожку из sourcePath в MP4 по outputPath
    public async Task remuxToMp4Async(string sourcePath, string outputPath, CancellationToken cancellationToken) {
        // -c copy сохраняет кодеки, -n запрещает перезапись, +faststart переносит индекс MP4 в начало файла
        await runner.runAsync(resolveExecutablePath(), new[] {
            "-hide_banner", "-loglevel", "error", "-nostdin", "-n", "-i", sourcePath,
            "-map", "0:v:0", "-map", "0:a:0?", "-c", "copy", "-movflags", "+faststart", "-f", "mp4", outputPath
        }, Path.GetDirectoryName(outputPath)!, cancellationToken);
    }
}
