using System.Globalization;
using System.Text.Json;
using MediaDownloader.Configuration;
using MediaDownloader.Exceptions;
using Microsoft.Extensions.Options;

namespace MediaDownloader.Tools;

// служба скачивания постов и альбомов через gallery-dl с проверкой полноты результата
public sealed class GalleryDlClient {
    private const string FilePrefix = "MEDIA_FILE:"; // маркер готового файла в stdout
    private const string SkipPrefix = "MEDIA_SKIPPED:"; // маркер пропущенного файла
    private readonly ExternalProcessRunner runner; // запуск CLI с отменой и сроком
    private readonly ToolOptions tools; // путь gallery-dl
    private readonly MediaOptions media; // предел размера и числа файлов

    // конструктор: сохраняем средство запуска и настройки Tools и Media
    public GalleryDlClient(ExternalProcessRunner runner, IOptions<ToolOptions> tools, IOptions<MediaOptions> media) {
        this.runner = runner;
        this.tools = tools.Value;
        this.media = media.Value;
    }

    // вызываем основной метод без предпочтения совместимого видео
    public Task<List<string>> downloadAsync(string url, string outputDirectory, CancellationToken cancellationToken) {
        return downloadAsync(url, outputDirectory, false, cancellationToken);
    }

    // скачиваем один пост в outputDirectory и возвращаем полный список путей файлов
    public async Task<List<string>> downloadAsync(string url, string outputDirectory, bool preferCompatibleVideo,
        CancellationToken cancellationToken) {
        // задаем папку, имя файла, лимиты и маркеры готовых или пропущенных файлов, игнорируем внешний конфиг
        List<string> arguments = new List<string> {
            "--config-ignore", "--no-input", "--quiet", "--no-colors", "--windows-filenames", "--cache-file", ":memory:",
            "--directory", outputDirectory, "--filename", "{id}_{num:>03}.{extension}",
            "--http-timeout", "15", "--retries", "0", "--filesize-max", media.MaxFileSizeBytes.ToString(CultureInfo.InvariantCulture),
            "--range", "1-" + (media.MaxFilesPerUrl + 1).ToString(CultureInfo.InvariantCulture),
            "-o", "extractor.tiktok.audio=false", "--Print", "after:MEDIA_FILE:{_path}",
            "--Print", "skip:MEDIA_SKIPPED:{_path}"
        };
        // при предпочтении совместимого видео добавляем обработку списка вариантов TikTok до скачивания
        if (preferCompatibleVideo) {
            arguments.Add("--option");
            arguments.Add(compatibleVideoPostprocessorOption());
        }
        arguments.Add("--");
        arguments.Add(url);
        ExternalProcessResult result = await runner.runAsync(tools.GalleryDlPath, arguments, outputDirectory, cancellationToken);
        string[] lines = result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        // усеченный вывод не позволяет подтвердить полноту скачивания, такой результат отклоняем
        if (result.OutputTruncated || result.ErrorTruncated) {
            throw new ExternalToolException("Не удалось проверить полноту результата загрузки.", "invalid_tool_output");
        }
        // если источник пропустил файл или превысил размер, вся ссылка считается неполной
        if (lines.Any(line => line.StartsWith(SkipPrefix, StringComparison.Ordinal)) ||
            result.StandardError.Contains("File size larger than allowed maximum", StringComparison.Ordinal)) {
            throw new ExternalToolException("Часть файлов пропущена инструментом; результат ссылки неполон.", "incomplete_media");
        }
        // after вызывается после finalize каждого файла; этот порядок задаёт extractor альбома.
        return lines.Where(line => line.StartsWith(FilePrefix, StringComparison.Ordinal))
            .Select(line => line[FilePrefix.Length..]).ToList();
    }

    // собираем настройку gallery-dl для предпочтения подходящих TikTok-видеовариантов
    private static string compatibleVideoPostprocessorOption() {
        // в метаданных оставляем H.264 с доступными URL и MP4 либо неизвестным Format; если подходящих нет, сохраняем исходный список
        return "extractor.tiktok.postprocessors=" + JsonSerializer.Serialize(new[] {
            new {
                name = "metadata", mode = "modify", @event = "post",
                filter = "locals().get('post_type') == 'video' and isinstance(locals().get('video'), dict) and bool(video.get('bitrateInfo'))",
                fields = new Dictionary<string, string> {
                    ["video[bitrateInfo]"] = "\fE (lambda entries: [e for e in entries if isinstance(e, dict) and str(e.get('CodecType', '')).lower() in ('h264', 'avc', 'avc1') and (not e.get('Format') or str(e['Format']).lower() == 'mp4') and isinstance(e.get('PlayAddr'), dict) and isinstance(e['PlayAddr'].get('UrlList'), list) and e['PlayAddr']['UrlList']] or entries)([video['bitrateInfo']] if isinstance(video.get('bitrateInfo'), dict) else video.get('bitrateInfo', []))"
                }
            }
        });
    }
}
