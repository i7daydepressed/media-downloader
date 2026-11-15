using System.Globalization;
using System.Text.Json;
using MediaDownloader.Configuration;
using MediaDownloader.Exceptions;
using Microsoft.Extensions.Options;

namespace MediaDownloader.Tools;

public sealed class GalleryDlClient {
    private const string FilePrefix = "MEDIA_FILE:";
    private const string SkipPrefix = "MEDIA_SKIPPED:";
    private readonly ExternalProcessRunner runner;
    private readonly ToolOptions tools;
    private readonly MediaOptions media;

    public GalleryDlClient(ExternalProcessRunner runner, IOptions<ToolOptions> tools, IOptions<MediaOptions> media) {
        this.runner = runner;
        this.tools = tools.Value;
        this.media = media.Value;
    }

    public Task<List<string>> downloadAsync(string url, string outputDirectory, CancellationToken cancellationToken) {
        return downloadAsync(url, outputDirectory, false, cancellationToken);
    }

    public async Task<List<string>> downloadAsync(string url, string outputDirectory, bool preferCompatibleVideo,
        CancellationToken cancellationToken) {
        List<string> arguments = new List<string> {
            "--config-ignore", "--no-input", "--quiet", "--no-colors", "--windows-filenames", "--cache-file", ":memory:",
            "--directory", outputDirectory, "--filename", "{id}_{num:>03}.{extension}",
            "--http-timeout", "15", "--retries", "0", "--filesize-max", media.MaxFileSizeBytes.ToString(CultureInfo.InvariantCulture),
            "--range", "1-" + (media.MaxFilesPerUrl + 1).ToString(CultureInfo.InvariantCulture),
            "-o", "extractor.tiktok.audio=false", "--Print", "after:MEDIA_FILE:{_path}",
            "--Print", "skip:MEDIA_SKIPPED:{_path}"
        };
        if (preferCompatibleVideo) {
            arguments.Add("--option");
            arguments.Add(compatibleVideoPostprocessorOption());
        }
        arguments.Add("--");
        arguments.Add(url);
        ExternalProcessResult result = await runner.runAsync(tools.GalleryDlPath, arguments, outputDirectory, cancellationToken);
        string[] lines = result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (result.OutputTruncated || result.ErrorTruncated) {
            throw new ExternalToolException("Не удалось проверить полноту результата загрузки.", "invalid_tool_output");
        }
        if (lines.Any(line => line.StartsWith(SkipPrefix, StringComparison.Ordinal)) ||
            result.StandardError.Contains("File size larger than allowed maximum", StringComparison.Ordinal)) {
            throw new ExternalToolException("Часть файлов пропущена инструментом; результат ссылки неполон.", "incomplete_media");
        }
        // after вызывается после finalize каждого файла; этот порядок задаёт extractor альбома.
        return lines.Where(line => line.StartsWith(FilePrefix, StringComparison.Ordinal))
            .Select(line => line[FilePrefix.Length..]).ToList();
    }

    private static string compatibleVideoPostprocessorOption() {
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
