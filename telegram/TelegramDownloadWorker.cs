using MediaDownloader.Configuration;
using MediaDownloader.Dto;
using MediaDownloader.Exceptions;
using MediaDownloader.Services;
using MediaDownloader.Tools;
using Microsoft.Extensions.Options;

namespace MediaDownloader.Telegram;

// фоновая служба: читаем очередь, скачиваем ссылки общим сервисом и отправляем файлы в чат
public sealed class TelegramDownloadWorker : BackgroundService {
    private readonly TelegramOptions options; // лимиты отправки и включение адаптера
    private readonly TelegramDownloadQueue queue; // ожидающие запросы на скачивание
    private readonly TelegramApiClient api; // отправка сообщений и файлов
    private readonly MediaService mediaService; // общий сценарий скачивания
    private readonly WorkingDirectoryManager directories; // проверка внутренних путей
    private readonly IHostApplicationLifetime lifetime; // остановка работы вместе с сервером
    private readonly ILogger<TelegramDownloadWorker> logger; // ошибки задания и доставки

    // конструктор: сохраняем очередь, API, сервис скачивания и настройки
    public TelegramDownloadWorker(IOptions<TelegramOptions> options, TelegramDownloadQueue queue, TelegramApiClient api,
        MediaService mediaService, WorkingDirectoryManager directories, IHostApplicationLifetime lifetime,
        ILogger<TelegramDownloadWorker> logger) {
        this.options = options.Value;
        this.queue = queue;
        this.api = api;
        this.mediaService = mediaService;
        this.directories = directories;
        this.lifetime = lifetime;
        this.logger = logger;
    }

    // при включенном адаптере последовательно выполняем задания из очереди до остановки
    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        if (!options.Enabled) {
            return;
        }
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, lifetime.ApplicationStopping);
        try {
            // чтение очереди ожидает новые задания, processJobAsync завершает одно перед получением следующего
            await foreach (TelegramDownloadJob job in queue.readAllAsync(linked.Token)) {
                await processJobAsync(job, linked.Token);
            }
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
    }

    // скачиваем ссылки задания, отправляем каждый файл и удаляем только подтвержденно доставленные файлы
    private async Task processJobAsync(TelegramDownloadJob job, CancellationToken cancellationToken) {
        MediaBatchResult? batch = null; // полученный результат для проверки оставшихся файлов в finally
        try {
            // true запрашивает предпочтение совместимого видео и попытку перепаковки; ошибки отдельных ссылок сообщаем в чат
            batch = await mediaService.downloadAsync(job.Request, true, cancellationToken);
            foreach (MediaResult result in batch.Results) {
                cancellationToken.ThrowIfCancellationRequested();
                if (!result.Success) {
                    await notifyAsync(job.ChatId, $"Ссылка {result.Index + 1}: {result.ErrorMessage}", cancellationToken);
                    continue;
                }
                foreach (MediaFileResult file in result.Files) {
                    cancellationToken.ThrowIfCancellationRequested();
                    try {
                        // проверяем путь и размер файла по лимиту выбранного режима Bot API
                        string path = directories.validateFilePath(batch.WorkingDirectory!, file.FilePath!);
                        long limit = options.UseLocalBotApi ? options.MaxUploadSizeBytes : Math.Min(options.MaxUploadSizeBytes, 50_000_000);
                        if (new FileInfo(path).Length > limit) {
                            await notifyAsync(job.ChatId, $"Ссылка {result.Index + 1}, файл {file.Index + 1}: превышен лимит отправки Telegram.", cancellationToken);
                            continue;
                        }
                        // совместимый MP4 отправляем через sendVideo, остальные файлы через sendDocument
                        if (file.IsCompatibleVideo) {
                            await api.sendVideoAsync(job.ChatId, path, file.FileName, file.DurationSeconds,
                                file.Width, file.Height, cancellationToken);
                        }
                        else {
                            await api.sendDocumentAsync(job.ChatId, path, file.FileName, cancellationToken);
                        }
                        // API уже подтвердил message_id; просим сервис удалить именно доставленный файл
                        await mediaService.deleteDeliveredFileAsync(batch, file);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
                        throw;
                    }
                    // при ошибке доставки сообщаем о неподтвержденном файле; задержку API выдерживаем перед следующим запросом
                    catch (TelegramApiException exception) {
                        logger.LogWarning("Telegram file delivery failed with {Code}.", exception.Code);
                        if (exception.RetryAfterSeconds is int delay) {
                            await TelegramApiClient.waitRetryAfterAsync(delay, cancellationToken);
                        }
                        await notifyAsync(job.ChatId, $"Ссылка {result.Index + 1}, файл {file.Index + 1}: отправка файла не подтверждена ({exception.Code}).", cancellationToken);
                    }
                    catch (Exception exception) {
                        logger.LogError("Telegram file delivery failed after {ExceptionType}.", exception.GetType().Name);
                        await notifyAsync(job.ChatId, $"Ссылка {result.Index + 1}, файл {file.Index + 1}: не удалось подтвердить отправку файла.", cancellationToken);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
            throw;
        }
        catch (BadRequestException exception) {
            await notifyAsync(job.ChatId, exception.PublicMessage, cancellationToken);
        }
        catch (Exception exception) {
            logger.LogError("Telegram job failed after {ExceptionType}.", exception.GetType().Name);
            await notifyAsync(job.ChatId, "Не удалось выполнить загрузку.", cancellationToken);
        }
        // всю папку очищаем, только если файлов больше нет; оставшиеся файлы сохраняются для стартовой очистки
        finally {
            if (batch is not null && !batch.Results.SelectMany(result => result.Files)
                .Any(file => file.FilePath is not null && File.Exists(file.FilePath))) {
                await mediaService.cleanupAsync(batch);
            }
            // Непереданные файлы остаются до следующего startup cleanup, включая shutdown во время upload.
        }
    }

    // отправляем сообщение о результате или ошибке; отказ API записываем и выдерживаем его задержку
    private async Task notifyAsync(long chatId, string text, CancellationToken cancellationToken) {
        try {
            await api.sendMessageAsync(chatId, text, cancellationToken);
        }
        catch (TelegramApiException exception) {
            logger.LogWarning("Telegram delivery notification failed with {Code}.", exception.Code);
            if (exception.RetryAfterSeconds is int delay) {
                await TelegramApiClient.waitRetryAfterAsync(delay, cancellationToken);
            }
        }
    }

    // закрываем прием заданий в очередь и передаем остановку фоновой службы .NET
    public override async Task StopAsync(CancellationToken cancellationToken) {
        queue.complete();
        await base.StopAsync(cancellationToken);
    }
}
