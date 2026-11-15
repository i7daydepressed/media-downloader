using System.Globalization;
using System.Text.Json;
using MediaDownloader.Configuration;
using MediaDownloader.Exceptions;
using Microsoft.Extensions.Options;

namespace MediaDownloader.Tools;

public sealed class FfprobeClient {
    private readonly ExternalProcessRunner runner;
    private readonly ToolOptions options;

    public FfprobeClient(ExternalProcessRunner runner, IOptions<ToolOptions> options) {
        this.runner = runner;
        this.options = options.Value;
    }

    public async Task<MediaProbeResult> probeAsync(string filePath, string workingDirectory, CancellationToken cancellationToken) {
        ExternalProcessResult result = await runner.runAsync(options.FfprobePath, new[] {
            "-v", "error", "-print_format", "json", "-show_format", "-show_streams",
            "-show_entries", "format=duration,format_name:format_tags=major_brand:stream=codec_type,codec_name,pix_fmt,width,height", filePath
        }, workingDirectory, cancellationToken);
        if (result.OutputTruncated) {
            throw invalidProbe();
        }
        try {
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
            if (!hasMedia) {
                throw invalidProbe();
            }
            double? duration = null;
            string? containerFormat = null;
            string? majorBrand = null;
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
            bool mp4Brand = majorBrand is "isom" or "iso2" or "iso3" or "iso4" or "iso5" or "iso6" or "iso7" or "iso8" or "iso9" or "mp41" or "mp42" or "avc1";
            bool compatible = Path.GetExtension(filePath).Equals(".mp4", StringComparison.OrdinalIgnoreCase) &&
                containerFormat?.Split(',').Contains("mp4", StringComparer.Ordinal) == true && mp4Brand &&
                videoStreams == 1 && videoCodec == "h264" && pixelFormat == "yuv420p" &&
                (audioStreams == 0 || (audioStreams == 1 && audioCodec == "aac")) &&
                duration is > 0 and <= int.MaxValue && width is > 0 && height is > 0;
            return new MediaProbeResult {
                DurationSeconds = duration, Width = width, Height = height,
                VideoCodec = videoCodec, AudioCodec = audioCodec, PixelFormat = pixelFormat,
                ContainerFormat = containerFormat, IsCompatibleVideo = compatible
            };
        }
        catch (JsonException) {
            throw invalidProbe();
        }
    }

    private static string? readString(JsonElement element, string name) {
        return element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;
    }

    private static int? readDimension(JsonElement stream, string name) {
        if (!stream.TryGetProperty(name, out JsonElement value)) {
            return null;
        }
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int dimension) || dimension < 1) {
            throw invalidProbe();
        }
        return dimension;
    }

    private static ExternalToolException invalidProbe() {
        return new ExternalToolException("Не удалось прочитать характеристики медиа.", "invalid_probe_output");
    }
}
