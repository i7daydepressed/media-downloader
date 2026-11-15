using MediaDownloader.Configuration;
using MediaDownloader.Dto;
using MediaDownloader.Exceptions;
using MediaDownloader.Services;
using Microsoft.Extensions.Options;

namespace MediaDownloader.Telegram;

public sealed class TelegramUpdateHandler {
    private readonly TelegramOptions options;
    private readonly TelegramApiClient api;
    private readonly TelegramDownloadQueue queue;
    private readonly MediaService mediaService;
    private readonly ILogger<TelegramUpdateHandler> logger;

    public TelegramUpdateHandler(IOptions<TelegramOptions> options, TelegramApiClient api,
        TelegramDownloadQueue queue, MediaService mediaService, ILogger<TelegramUpdateHandler> logger) {
        this.options = options.Value;
        this.api = api;
        this.queue = queue;
        this.mediaService = mediaService;
        this.logger = logger;
    }

    public async Task handleAsync(TelegramUpdate update, CancellationToken cancellationToken) {
        TelegramMessage? message = update.Message;
        if (message?.Chat is null || message.Chat.Type != "private" || message.Chat.Id <= 0) {
            return;
        }
        try {
            if (message.From is null || !options.AllowedUserIds.Contains(message.From.Id)) {
                throw new AccessDeniedException();
            }
            if (string.IsNullOrWhiteSpace(message.Text)) {
                throw new BadRequestException("Пришлите только HTTP/HTTPS-ссылки через пробелы или переносы строк.");
            }
            MediaRequest request = new MediaRequest {
                Urls = message.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToList()
            };
            request = new MediaRequest { Urls = mediaService.validateRequest(request) };
            if (!queue.tryEnqueue(new TelegramDownloadJob { ChatId = message.Chat.Id, Request = request })) {
                await notifyAsync(message.Chat.Id, "Очередь заполнена. Повторите запрос позже.", cancellationToken);
            }
        }
        catch (AccessDeniedException exception) {
            await notifyAsync(message.Chat.Id, exception.PublicMessage, cancellationToken);
        }
        catch (BadRequestException exception) {
            await notifyAsync(message.Chat.Id, exception.PublicMessage, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
            throw;
        }
        catch (Exception exception) {
            logger.LogError("Telegram update rejected after {ExceptionType}.", exception.GetType().Name);
            await notifyAsync(message.Chat.Id, "Не удалось обработать сообщение.", cancellationToken);
        }
    }

    private async Task notifyAsync(long chatId, string text, CancellationToken cancellationToken) {
        try {
            await api.sendMessageAsync(chatId, text, cancellationToken);
        }
        catch (TelegramApiException exception) {
            logger.LogWarning("Telegram rejection notification failed with {Code}.", exception.Code);
            if (exception.RetryAfterSeconds is int delay) {
                await TelegramApiClient.waitRetryAfterAsync(delay, cancellationToken);
            }
        }
    }
}
