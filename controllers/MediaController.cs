using MediaDownloader.Dto;
using MediaDownloader.Exceptions;
using MediaDownloader.Services;
using MediaDownloader.Tools;
using Microsoft.AspNetCore.Mvc;

namespace MediaDownloader.Controllers;

[ApiController]
[Route("api/media")]
public sealed class MediaController : ControllerBase {
    private readonly MediaService mediaService;
    private readonly MediaArchiveWriter archiveWriter;
    private readonly IHostApplicationLifetime lifetime;
    private readonly ILogger<TemporaryArchiveResult> archiveLogger;

    public MediaController(MediaService mediaService, MediaArchiveWriter archiveWriter,
        IHostApplicationLifetime lifetime, ILogger<TemporaryArchiveResult> archiveLogger) {
        this.mediaService = mediaService;
        this.archiveWriter = archiveWriter;
        this.lifetime = lifetime;
        this.archiveLogger = archiveLogger;
    }

    [HttpPost("download")]
    public async Task<IActionResult> downloadAsync([FromBody] MediaRequest request) {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            HttpContext.RequestAborted, lifetime.ApplicationStopping);
        MediaBatchResult? batch = null;
        try {
            batch = await mediaService.downloadAsync(request, linked.Token);
            string archivePath = await archiveWriter.createArchiveAsync(batch, linked.Token);
            TemporaryArchiveResult result = new TemporaryArchiveResult(batch, archivePath,
                mediaService, archiveWriter, lifetime, archiveLogger);
            batch = null;
            return result;
        }
        catch {
            if (batch is not null) {
                await mediaService.cleanupAsync(batch);
            }
            linked.Token.ThrowIfCancellationRequested();
            throw;
        }
    }

    [HttpPost("download/links")]
    public IActionResult downloadLinks() {
        return StatusCode(StatusCodes.Status501NotImplemented,
            GlobalExceptionHandler.createResponse(HttpContext, "not_implemented",
                "Отдельные ссылки на файлы и хранение результатов пока не реализованы. Используйте прямой ZIP-ответ.", 501));
    }
}
