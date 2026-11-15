using MediaDownloader.Dto;
using MediaDownloader.Services;
using MediaDownloader.Tools;
using Microsoft.AspNetCore.Mvc;

namespace MediaDownloader.Controllers;

// HTTP-результат для передачи ZIP потоком; IActionResult вызывает ExecuteResultAsync при формировании ответа
public sealed class TemporaryArchiveResult : IActionResult {
    private readonly MediaBatchResult batch; // результаты и папка задания для последующей очистки
    private readonly string archivePath; // полный путь временного ZIP
    private readonly MediaService mediaService; // очистка папки задания
    private readonly MediaArchiveWriter archiveWriter; // проверка принадлежности ZIP результату
    private readonly IHostApplicationLifetime lifetime; // остановка передачи при завершении сервера
    private readonly ILogger<TemporaryArchiveResult> logger; // ошибки уже начатого ответа

    // конструктор: принимаем готовый ZIP и ответственность за очистку batch
    public TemporaryArchiveResult(MediaBatchResult batch, string archivePath, MediaService mediaService,
        MediaArchiveWriter archiveWriter, IHostApplicationLifetime lifetime, ILogger<TemporaryArchiveResult> logger) {
        this.batch = batch;
        this.archivePath = archivePath;
        this.mediaService = mediaService;
        this.archiveWriter = archiveWriter;
        this.lifetime = lifetime;
        this.logger = logger;
    }

    // передаем проверенный ZIP в HTTP-ответ и пытаемся очистить папку задания при любом исходе
    public async Task ExecuteResultAsync(ActionContext context) {
        HttpContext httpContext = context.HttpContext;
        HttpResponse response = httpContext.Response;
        // отменяем передачу при обрыве запроса или остановке сервера
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            httpContext.RequestAborted, lifetime.ApplicationStopping);
        try {
            linked.Token.ThrowIfCancellationRequested();
            // проверяем путь архива и открываем файл для последовательного чтения
            string path = archiveWriter.validateArchivePath(batch, archivePath);
            await using FileStream input = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.Read, 65_536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            // задаем тип, имя и размер вложения, затем копируем файл в тело ответа
            response.ContentType = "application/zip";
            response.Headers.ContentDisposition = "attachment; filename=\"media-downloader.zip\"";
            response.Headers.CacheControl = "no-store";
            response.ContentLength = input.Length;
            await input.CopyToAsync(response.Body, 65_536, linked.Token);
            await response.Body.FlushAsync(linked.Token);
        }
        // при отмене начатого или прерванного клиентом ответа обрываем соединение; иначе передаем ошибку выше
        catch (OperationCanceledException) when (linked.IsCancellationRequested) {
            if (response.HasStarted || httpContext.RequestAborted.IsCancellationRequested) {
                httpContext.Abort();
                return;
            }
            response.Clear();
            throw;
        }
        // после начала ответа нельзя заменить ZIP JSON-ошибкой, поэтому фиксируем тип ошибки и закрываем соединение
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
        // после передачи, ошибки или отмены просим MediaService очистить файлы batch
        finally {
            await mediaService.cleanupAsync(batch);
        }
    }
}
