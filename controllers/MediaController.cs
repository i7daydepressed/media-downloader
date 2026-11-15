using MediaDownloader.Dto;
using MediaDownloader.Exceptions;
using MediaDownloader.Services;
using MediaDownloader.Tools;
using Microsoft.AspNetCore.Mvc;

namespace MediaDownloader.Controllers;

// HTTP-вход: передаем ссылки общему сервису и возвращаем ZIP с результатами скачивания
[ApiController]
[Route("api/media")]
public sealed class MediaController : ControllerBase {
    private readonly MediaService mediaService; // общий сценарий скачивания и очистки
    private readonly MediaArchiveWriter archiveWriter; // сборка и проверка временного ZIP
    private readonly IHostApplicationLifetime lifetime; // сигнал остановки сервера
    private readonly ILogger<TemporaryArchiveResult> archiveLogger; // ошибки передачи ZIP клиенту

    // конструктор: сохраняем сервис, службу архива и зависимости передачи ответа
    public MediaController(MediaService mediaService, MediaArchiveWriter archiveWriter,
        IHostApplicationLifetime lifetime, ILogger<TemporaryArchiveResult> archiveLogger) {
        this.mediaService = mediaService;
        this.archiveWriter = archiveWriter;
        this.lifetime = lifetime;
        this.archiveLogger = archiveLogger;
    }

    // принимаем JSON с MediaRequest и возвращаем скачанные файлы архивом
    [HttpPost("download")]
    public async Task<IActionResult> downloadAsync([FromBody] MediaRequest request) {
        // связываем обрыв HTTP-запроса и остановку сервера для скачивания, ZIP и передачи ответа
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            HttpContext.RequestAborted, lifetime.ApplicationStopping);
        MediaBatchResult? batch = null; // полученный результат, за очистку которого пока отвечает контроллер
        try {
            // скачиваем ссылки через MediaService, затем создаем ZIP по описаниям и путям файлов
            batch = await mediaService.downloadAsync(request, linked.Token);
            string archivePath = await archiveWriter.createArchiveAsync(batch, linked.Token);
            // передаем ZIP и batch в TemporaryArchiveResult: он отправит архив и очистит папку после передачи
            TemporaryArchiveResult result = new TemporaryArchiveResult(batch, archivePath,
                mediaService, archiveWriter, lifetime, archiveLogger);
            batch = null; // передали ответственность за очистку объекту результата
            return result;
        }
        // если результат еще принадлежит контроллеру, пробуем очистить его перед передачей ошибки выше
        catch {
            if (batch is not null) {
                await mediaService.cleanupAsync(batch);
            }
            linked.Token.ThrowIfCancellationRequested();
            throw;
        }
    }

    // возвращаем 501: хранение результатов и отдельные ссылки на файлы пока не реализованы
    [HttpPost("download/links")]
    public IActionResult downloadLinks() {
        return StatusCode(StatusCodes.Status501NotImplemented,
            GlobalExceptionHandler.createResponse(HttpContext, "not_implemented",
                "Отдельные ссылки на файлы и хранение результатов пока не реализованы. Используйте прямой ZIP-ответ.", 501));
    }
}
