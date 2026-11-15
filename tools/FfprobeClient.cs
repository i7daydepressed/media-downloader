using System.Globalization;
using System.Text.Json;
using MediaDownloader.Configuration;
using MediaDownloader.Exceptions;
using Microsoft.Extensions.Options;

namespace MediaDownloader.Tools;

// читаем JSON FFprobe и возвращаем проверенные характеристики медиафайла
public sealed class FfprobeClient {
    private readonly ExternalProcessRunner runner; // запуск FFprobe с отменой и сроком
    private readonly ToolOptions options; // путь FFprobe из раздела Tools

    // конструктор: сохраняем средство запуска и настройки утилиты
    public FfprobeClient(ExternalProcessRunner runner, IOptions<ToolOptions> options) {
        this.runner = runner;
        this.options = options.Value;
    }

    // получаем контейнер, дорожки, длительность и размеры файла, вычисляем признаки совместимости
    public async Task<MediaProbeResult> probeAsync(string filePath, string workingDirectory, CancellationToken cancellationToken) {
        // запрашиваем только используемые поля format и streams, усеченный JSON отклоняем
        ExternalProcessResult result = await runner.runAsync(options.FfprobePath, new[] {
            "-v", "error", "-print_format", "json", "-show_format", "-show_streams",
            "-show_entries", "format=duration,format_name:format_tags=major_brand:stream=codec_type,codec_name,pix_fmt,width,height", filePath
        }, workingDirectory, cancellationToken);
        if (result.OutputTruncated) {
            throw invalidProbe();
        }
        try {
            // разбираем объект JSON и требуем массив streams с описаниями дорожек
            using JsonDocument document = JsonDocument.Parse(result.StandardOutput);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("streams", out JsonElement streams) ||
                streams.ValueKind != JsonValueKind.Array) {
                throw invalidProbe();
            }
            bool hasMedia = false;
            int? width = null;
            int? height = null;
            string? videoCodec = null;
            string? audioCodec = null;
            string? pixelFormat = null;
            int videoStreams = 0;
            int audioStreams = 0;
            // считаем видео- и аудиодорожки, сохраняем первые кодеки, формат пикселей и размеры видео
            foreach (JsonElement stream in streams.EnumerateArray()) {
                if (stream.ValueKind != JsonValueKind.Object || !stream.TryGetProperty("codec_type", out JsonElement codecType) ||
                    codecType.ValueKind != JsonValueKind.String) {
                    throw invalidProbe();
                }
                string? type = codecType.GetString();
                hasMedia |= type is "video" or "audio";
                if (type == "video") {
                    videoStreams++;
                    if (videoStreams == 1) {
                        videoCodec = readString(stream, "codec_name");
                        pixelFormat = readString(stream, "pix_fmt");
                    }
                }
                if (type == "audio") {
                    audioStreams++;
                    if (audioStreams == 1) {
                        audioCodec = readString(stream, "codec_name");
                    }
                }
                if (type == "video" && width is null) {
                    width = readDimension(stream, "width");
                    height = readDimension(stream, "height");
                }
            }
            // без аудио или видео файл не считается медиа
            if (!hasMedia) {
                throw invalidProbe();
            }
            double? duration = null;
            string? containerFormat = null;
            string? majorBrand = null;
            // из format читаем контейнер, major_brand и длительность; неверное число отклоняем
            if (root.TryGetProperty("format", out JsonElement format)) {
                if (format.ValueKind != JsonValueKind.Object) {
                    throw invalidProbe();
                }
                containerFormat = readString(format, "format_name");
                if (format.TryGetProperty("tags", out JsonElement tags) && tags.ValueKind == JsonValueKind.Object) {
                    majorBrand = readString(tags, "major_brand")?.Trim().ToLowerInvariant();
                }
                if (format.TryGetProperty("duration", out JsonElement value)) {
                    string? text = value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
                    if (text is not null && text != "N/A") {
                        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) ||
                            !double.IsFinite(parsed) || parsed < 0) {
                            throw invalidProbe();
                        }
                        duration = parsed;
                    }
                }
            }
            // проверяем марку MP4, одно H.264/yuv420p-видео, AAC либо отсутствие звука и допустимые размеры/длительность
            bool mp4Brand = majorBrand is "isom" or "iso2" or "iso3" or "iso4" or "iso5" or "iso6" or "iso7" or "iso8" or "iso9" or "mp41" or "mp42" or "avc1";
            bool compatibleStreams = videoStreams == 1 && videoCodec == "h264" && pixelFormat == "yuv420p" &&
                (audioStreams == 0 || (audioStreams == 1 && audioCodec == "aac")) &&
                duration is > 0 and <= int.MaxValue && width is > 0 && height is > 0;
            bool compatible = Path.GetExtension(filePath).Equals(".mp4", StringComparison.OrdinalIgnoreCase) &&
                containerFormat?.Split(',').Contains("mp4", StringComparer.Ordinal) == true && mp4Brand && compatibleStreams;
            // собираем характеристики; HasCompatibleMp4Streams дополнительно исключает посторонние потоки
            return new MediaProbeResult {
                DurationSeconds = duration, Width = width, Height = height,
                VideoCodec = videoCodec, AudioCodec = audioCodec, PixelFormat = pixelFormat,
                ContainerFormat = containerFormat, IsCompatibleVideo = compatible,
                HasCompatibleMp4Streams = compatibleStreams && streams.GetArrayLength() == videoStreams + audioStreams
            };
        }
        catch (JsonException) {
            throw invalidProbe();
        }
    }

    // возвращаем строковое поле JSON либо null при его отсутствии или другом типе
    private static string? readString(JsonElement element, string name) {
        return element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;
    }

    // читаем положительный размер кадра; отсутствующее поле дает null, неверное вызывает ошибку
    private static int? readDimension(JsonElement stream, string name) {
        if (!stream.TryGetProperty(name, out JsonElement value)) {
            return null;
        }
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int dimension) || dimension < 1) {
            throw invalidProbe();
        }
        return dimension;
    }

    // создаем ошибку invalid_probe_output для непригодного ответа FFprobe
    private static ExternalToolException invalidProbe() {
        return new ExternalToolException("Не удалось прочитать характеристики медиа.", "invalid_probe_output");
    }
}
