using MediaDownloader.Dto;
using MediaDownloader.Services;
using MediaDownloader.Tools;
using Microsoft.AspNetCore.Mvc;

namespace MediaDownloader.Controllers;

public sealed class TemporaryArchiveResult : IActionResult {
    private readonly MediaBatchResult batch;
    private readonly string archivePath;
    private readonly MediaService mediaService;
    private readonly MediaArchiveWriter archiveWriter;
    private readonly IHostApplicationLifetime lifetime;
    private readonly ILogger<TemporaryArchiveResult> logger;

    public TemporaryArchiveResult(MediaBatchResult batch, string archivePath, MediaService mediaService,
        MediaArchiveWriter archiveWriter, IHostApplicationLifetime lifetime, ILogger<TemporaryArchiveResult> logger) {
        this.batch = batch;
        this.archivePath = archivePath;
        this.mediaService = mediaService;
        this.archiveWriter = archiveWriter;
        this.lifetime = lifetime;
        this.logger = logger;
    }

    public async Task ExecuteResultAsync(ActionContext context) {
        HttpContext httpContext = context.HttpContext;
        HttpResponse response = httpContext.Response;
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            httpContext.RequestAborted, lifetime.ApplicationStopping);
        try {
            linked.Token.ThrowIfCancellationRequested();
            string path = archiveWriter.validateArchivePath(batch, archivePath);
            await using FileStream input = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.Read, 65_536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            response.ContentType = "application/zip";
            response.Headers.ContentDisposition = "attachment; filename=\"media-downloader.zip\"";
            response.Headers.CacheControl = "no-store";
            response.ContentLength = input.Length;
            await input.CopyToAsync(response.Body, 65_536, linked.Token);
            await response.Body.FlushAsync(linked.Token);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) {
            if (response.HasStarted || httpContext.RequestAborted.IsCancellationRequested) {
                httpContext.Abort();
                return;
            }
            response.Clear();
            throw;
        }
        catch (Exception exception) {
            if (response.HasStarted || httpContext.RequestAborted.IsCancellationRequested) {
                logger.LogWarning("Archive response stopped after {ExceptionType} for request {TraceId}.",
                    exception.GetType().Name, httpContext.TraceIdentifier);
                httpContext.Abort();
                return;
            }
            // Иначе обработчик ошибки должен получить чистые заголовки вместо длины ZIP.
            response.Clear();
            linked.Token.ThrowIfCancellationRequested();
            throw;
        }
        finally {
            await mediaService.cleanupAsync(batch);
        }
    }
}
