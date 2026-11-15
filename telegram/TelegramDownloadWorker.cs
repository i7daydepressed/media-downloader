using MediaDownloader.Configuration;
using MediaDownloader.Dto;
using MediaDownloader.Exceptions;
using MediaDownloader.Services;
using MediaDownloader.Tools;
using Microsoft.Extensions.Options;

namespace MediaDownloader.Telegram;

public sealed class TelegramDownloadWorker : BackgroundService {
    private readonly TelegramOptions options;
    private readonly TelegramDownloadQueue queue;
    private readonly TelegramApiClient api;
    private readonly MediaService mediaService;
    private readonly WorkingDirectoryManager directories;
    private readonly IHostApplicationLifetime lifetime;
    private readonly ILogger<TelegramDownloadWorker> logger;

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

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        if (!options.Enabled) {
            return;
        }
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, lifetime.ApplicationStopping);
        try {
            await foreach (TelegramDownloadJob job in queue.readAllAsync(linked.Token)) {
                await processJobAsync(job, linked.Token);
            }
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
    }

    private async Task processJobAsync(TelegramDownloadJob job, CancellationToken cancellationToken) {
        MediaBatchResult? batch = null;
        try {
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
                        string path = directories.validateFilePath(batch.WorkingDirectory!, file.FilePath!);
                        long limit = options.UseLocalBotApi ? options.MaxUploadSizeBytes : Math.Min(options.MaxUploadSizeBytes, 50_000_000);
                        if (new FileInfo(path).Length > limit) {
                            await notifyAsync(job.ChatId, $"Ссылка {result.Index + 1}, файл {file.Index + 1}: превышен лимит отправки Telegram.", cancellationToken);
                            continue;
                        }
                        if (file.IsCompatibleVideo) {
                            await api.sendVideoAsync(job.ChatId, path, file.FileName, file.DurationSeconds,
                                file.Width, file.Height, cancellationToken);
                        }
                        else {
                            await api.sendDocumentAsync(job.ChatId, path, file.FileName, cancellationToken);
                        }
                        await mediaService.deleteDeliveredFileAsync(batch, file);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
                        throw;
                    }
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
        finally {
            if (batch is not null && !batch.Results.SelectMany(result => result.Files)
                .Any(file => file.FilePath is not null && File.Exists(file.FilePath))) {
                await mediaService.cleanupAsync(batch);
            }
            // Непереданные файлы остаются до следующего startup cleanup, включая shutdown во время upload.
        }
    }

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

    public override async Task StopAsync(CancellationToken cancellationToken) {
        queue.complete();
        await base.StopAsync(cancellationToken);
    }
}
