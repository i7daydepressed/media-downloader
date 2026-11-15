using MediaDownloader.Configuration;
using MediaDownloader.Dto;
using MediaDownloader.Exceptions;
using MediaDownloader.Tools;
using Microsoft.Extensions.Options;

namespace MediaDownloader.Services;

public sealed class MediaPreparationService {
    private readonly MediaOptions options;
    private readonly FfmpegClient ffmpeg;
    private readonly FfprobeClient ffprobe;
    private readonly WorkingDirectoryManager directories;
    private readonly ILogger<MediaPreparationService> logger;

    public MediaPreparationService(IOptions<MediaOptions> options, FfmpegClient ffmpeg, FfprobeClient ffprobe,
        WorkingDirectoryManager directories, ILogger<MediaPreparationService> logger) {
        this.options = options.Value;
        this.ffmpeg = ffmpeg;
        this.ffprobe = ffprobe;
        this.directories = directories;
        this.logger = logger;
    }

    public async Task<List<MediaFileResult>> prepareAsync(string jobDirectory, List<MediaFileResult> files,
        CancellationToken cancellationToken) {
        List<MediaFileResult> prepared = new List<MediaFileResult>(files.Count);
        foreach (MediaFileResult file in files) {
            cancellationToken.ThrowIfCancellationRequested();
            prepared.Add(await prepareFileAsync(jobDirectory, file, cancellationToken));
        }
        return prepared;
    }

    private async Task<MediaFileResult> prepareFileAsync(string jobDirectory, MediaFileResult original,
        CancellationToken cancellationToken) {
        if (original.FilePath is null || !Path.GetExtension(original.FilePath).Equals(".mkv", StringComparison.OrdinalIgnoreCase)) {
            return original;
        }
        string? candidateDirectory = null;
        bool accepted = false;
        try {
            string sourcePath = directories.validateFilePath(jobDirectory, original.FilePath);
            MediaProbeResult source = await ffprobe.probeAsync(sourcePath, Path.GetDirectoryName(sourcePath)!, cancellationToken);
            if (!source.HasCompatibleMp4Streams || source.ContainerFormat?.Split(',').Contains("matroska", StringComparer.Ordinal) != true) {
                return original;
            }
            candidateDirectory = directories.createPreparationDirectory(jobDirectory, sourcePath);
            string candidatePath = Path.Combine(candidateDirectory, "media.mp4");
            await ffmpeg.remuxToMp4Async(sourcePath, candidatePath, cancellationToken);
            candidatePath = directories.validateFilePath(jobDirectory, candidatePath, candidateDirectory);
            long size = new FileInfo(candidatePath).Length;
            if (size == 0 || size > options.MaxFileSizeBytes) {
                throw invalidPreparedMedia();
            }
            MediaProbeResult candidate = await ffprobe.probeAsync(candidatePath, candidateDirectory, cancellationToken);
            if (!candidate.IsCompatibleVideo || !candidate.HasCompatibleMp4Streams || !preservesMedia(source, candidate)) {
                throw invalidPreparedMedia();
            }
            MediaFileResult prepared = new MediaFileResult {
                Index = original.Index, FileName = Path.ChangeExtension(original.FileName, ".mp4"), SizeBytes = size,
                DurationSeconds = candidate.DurationSeconds, Width = candidate.Width, Height = candidate.Height,
                FilePath = candidatePath, IsCompatibleVideo = true
            };
            cancellationToken.ThrowIfCancellationRequested();
            if (!await directories.deleteOwnedFileAsync(jobDirectory, sourcePath)) {
                return original;
            }
            accepted = true;
            return prepared;
        }
        catch (Exception exception) when (exception is ExternalToolException or IOException or InvalidDataException or
            UnauthorizedAccessException or ArgumentException or NotSupportedException) {
            logger.LogWarning("Media preparation retains original after {Code}.",
                exception is ExternalToolException tool ? tool.Code : exception.GetType().Name);
            return original;
        }
        finally {
            if (!accepted && candidateDirectory is not null) {
                await directories.cleanupAttemptAsync(jobDirectory, candidateDirectory);
            }
        }
    }

    private static bool preservesMedia(MediaProbeResult source, MediaProbeResult candidate) {
        return source.VideoCodec == candidate.VideoCodec && source.AudioCodec == candidate.AudioCodec &&
            source.PixelFormat == candidate.PixelFormat && source.Width == candidate.Width && source.Height == candidate.Height &&
            source.DurationSeconds is double before && candidate.DurationSeconds is double after &&
            Math.Abs(before - after) <= Math.Max(0.1, Math.Min(0.5, before * 0.01));
    }

    private static ExternalToolException invalidPreparedMedia() {
        return new ExternalToolException("Prepared media did not preserve the source properties.", "invalid_prepared_media");
    }
}
