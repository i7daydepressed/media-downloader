using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using MediaDownloader.Configuration;
using MediaDownloader.Dto;
using MediaDownloader.Exceptions;
using MediaDownloader.Tools;
using Microsoft.Extensions.Options;

namespace MediaDownloader.Services;

public sealed class MediaService : IDisposable {
    private readonly MediaOptions options;
    private readonly YtDlpClient ytDlp;
    private readonly GalleryDlClient galleryDl;
    private readonly FfprobeClient ffprobe;
    private readonly MediaPreparationService preparation;
    private readonly WorkingDirectoryManager directories;
    private readonly IHostApplicationLifetime lifetime;
    private readonly ILogger<MediaService> logger;
    private readonly SemaphoreSlim gate;

    public MediaService(IOptions<MediaOptions> options, YtDlpClient ytDlp, GalleryDlClient galleryDl,
        FfprobeClient ffprobe, MediaPreparationService preparation, WorkingDirectoryManager directories, IHostApplicationLifetime lifetime,
        ILogger<MediaService> logger) {
        this.options = options.Value;
        this.ytDlp = ytDlp;
        this.galleryDl = galleryDl;
        this.ffprobe = ffprobe;
        this.preparation = preparation;
        this.directories = directories;
        this.lifetime = lifetime;
        this.logger = logger;
        gate = new SemaphoreSlim(this.options.MaxConcurrentOperations, this.options.MaxConcurrentOperations);
    }

    public Task<MediaBatchResult> downloadAsync(MediaRequest request, CancellationToken cancellationToken) {
        return downloadAsync(request, false, cancellationToken);
    }

    public async Task<MediaBatchResult> downloadAsync(MediaRequest request, bool preferCompatibleVideo,
        CancellationToken cancellationToken) {
        List<string> urls = validateRequest(request);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, lifetime.ApplicationStopping);
        CancellationToken operationToken = linked.Token;
        await gate.WaitAsync(operationToken);
        string? jobDirectory = null;
        try {
            operationToken.ThrowIfCancellationRequested();
            jobDirectory = directories.createJobDirectory();
            List<MediaResult> results = new List<MediaResult>();
            for (int index = 0; index < urls.Count; index++) {
                operationToken.ThrowIfCancellationRequested();
                results.Add(await downloadItemAsync(jobDirectory, index, urls[index], preferCompatibleVideo, operationToken));
            }
            operationToken.ThrowIfCancellationRequested();
            // Папкой теперь владеет адаптер, который завершает доставку и вызывает cleanupAsync.
            return new MediaBatchResult { Results = results, WorkingDirectory = jobDirectory };
        }
        catch {
            await directories.cleanupJobAsync(jobDirectory);
            throw;
        }
        finally {
            gate.Release();
        }
    }

    public Task<bool> cleanupAsync(MediaBatchResult batch) {
        return directories.cleanupJobAsync(batch.WorkingDirectory);
    }

    public Task<bool> deleteDeliveredFileAsync(MediaBatchResult batch, MediaFileResult file) {
        if (batch.WorkingDirectory is null || file.FilePath is null ||
            !batch.Results.SelectMany(result => result.Files).Contains(file)) {
            return Task.FromResult(false);
        }
        return directories.deleteDeliveredFileAsync(batch.WorkingDirectory, file.FilePath);
    }

    private async Task<MediaResult> downloadItemAsync(string jobDirectory, int index, string url, bool preferCompatibleVideo,
        CancellationToken cancellationToken) {
        string firstAttempt = directories.createAttemptDirectory(jobDirectory, index, "yt-dlp");
        try {
            List<string> paths = await ytDlp.downloadAsync(url, firstAttempt, preferCompatibleVideo, cancellationToken);
            if (paths.Count != 1) {
                throw new ExternalToolException("Источник не вернул один итоговый видеофайл.", "output_missing");
            }
            List<MediaFileResult> files = await validateFilesAsync(jobDirectory, firstAttempt, index, paths, cancellationToken);
            if (preferCompatibleVideo) {
                files = await preparation.prepareAsync(jobDirectory, files, cancellationToken);
            }
            return new MediaResult { Index = index, Url = url, Success = true, Files = files };
        }
        catch (ExternalToolException exception) {
            logger.LogInformation("Media item {Index} uses fallback after {Code}.", index, exception.Code);
            if (!await directories.cleanupAttemptAsync(jobDirectory, firstAttempt)) {
                throw new IOException("Не удалось очистить неуспешную попытку загрузки.");
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        string secondAttempt = directories.createAttemptDirectory(jobDirectory, index, "gallery-dl");
        try {
            List<string> paths = await galleryDl.downloadAsync(url, secondAttempt, preferCompatibleVideo, cancellationToken);
            List<MediaFileResult> files = await validateFilesAsync(jobDirectory, secondAttempt, index, paths, cancellationToken);
            if (preferCompatibleVideo) {
                files = await preparation.prepareAsync(jobDirectory, files, cancellationToken);
            }
            return new MediaResult { Index = index, Url = url, Success = true, Files = files };
        }
        catch (ExternalToolException exception) {
            if (!await directories.cleanupAttemptAsync(jobDirectory, secondAttempt)) {
                throw new IOException("Не удалось очистить неуспешную попытку загрузки.");
            }
            return new MediaResult {
                Index = index, Url = url, Success = false, ErrorCode = exception.Code, ErrorMessage = exception.PublicMessage
            };
        }
    }

    private async Task<List<MediaFileResult>> validateFilesAsync(string jobDirectory, string attemptDirectory,
        int urlIndex, List<string> paths, CancellationToken cancellationToken) {
        if (paths.Count == 0) {
            throw new ExternalToolException("Источник не вернул готовых медиафайлов.", "output_missing");
        }
        if (paths.Count > options.MaxFilesPerUrl) {
            throw new ExternalToolException("Превышено допустимое число файлов для одной ссылки.", "too_many_files");
        }
        HashSet<string> seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        List<MediaFileResult> files = new List<MediaFileResult>();
        for (int fileIndex = 0; fileIndex < paths.Count; fileIndex++) {
            cancellationToken.ThrowIfCancellationRequested();
            string path = directories.validateFilePath(jobDirectory, paths[fileIndex], attemptDirectory);
            string extension = Path.GetExtension(path).ToLowerInvariant();
            if (!Regex.IsMatch(extension, "^\\.[a-z0-9]{1,12}$") ||
                extension is ".part" or ".ytdl" or ".json" or ".txt" or ".tmp" or ".log" or ".srt" or ".vtt") {
                throw new ExternalToolException("Инструмент вернул служебный файл вместо медиа.", "invalid_media_file");
            }
            if (!seen.Add(path)) {
                throw new ExternalToolException("Список результата содержит повторяющийся файл.", "invalid_tool_output");
            }
            long size = new FileInfo(path).Length;
            if (size == 0 || size > options.MaxFileSizeBytes) {
                throw new ExternalToolException("Итоговый файл пуст или превышает допустимый размер.", "file_size_limit");
            }
            MediaProbeResult probe = await ffprobe.probeAsync(path, attemptDirectory, cancellationToken);
            files.Add(new MediaFileResult {
                Index = fileIndex,
                FileName = $"{urlIndex + 1:D4}-{fileIndex + 1:D4}{extension}",
                SizeBytes = size, DurationSeconds = probe.DurationSeconds, Width = probe.Width, Height = probe.Height,
                FilePath = path, IsCompatibleVideo = probe.IsCompatibleVideo
            });
        }
        return files;
    }

    public List<string> validateRequest(MediaRequest? request) {
        if (request?.Urls is null || request.Urls.Count == 0 || request.Urls.Count > options.MaxUrlsPerBatch) {
            throw new BadRequestException($"Передайте от 1 до {options.MaxUrlsPerBatch} ссылок в массиве urls.");
        }
        List<string> urls = new List<string>(request.Urls.Count);
        foreach (string? value in request.Urls) {
            if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsWhiteSpace) || value.Any(char.IsControl) ||
                value.Contains('\\') || !Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) || string.IsNullOrEmpty(uri.Host) ||
                !string.IsNullOrEmpty(uri.UserInfo)) {
                throw new BadRequestException("Каждый элемент urls должен быть HTTP/HTTPS-ссылкой без учётных данных.");
            }
            if (!options.AllowLocalUrls && isLocalAddress(uri)) {
                throw new BadRequestException("Локальные и приватные адреса источников запрещены.", "local_url_forbidden");
            }
            urls.Add(value);
        }
        return urls;
    }

    private static bool isLocalAddress(Uri uri) {
        string host = uri.IdnHost.TrimEnd('.').Trim('[', ']');
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)) {
            return true;
        }
        if (!IPAddress.TryParse(host, out IPAddress? address)) {
            return false;
        }
        if (address.IsIPv4MappedToIPv6) {
            address = address.MapToIPv4();
        }
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)) {
            return true;
        }
        byte[] bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork) {
            return bytes[0] is 0 or 10 or 127 || bytes[0] >= 224 ||
                (bytes[0] == 100 && bytes[1] is >= 64 and <= 127) ||
                (bytes[0] == 169 && bytes[1] == 254) ||
                (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
                (bytes[0] == 192 && bytes[1] == 168) ||
                (bytes[0] == 198 && bytes[1] is 18 or 19);
        }
        return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast || (bytes[0] & 0xfe) == 0xfc;
    }

    public void Dispose() {
        gate.Dispose();
    }
}
