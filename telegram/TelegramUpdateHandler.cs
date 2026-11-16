using MediaDownloader.Configuration;
using MediaDownloader.Annotations;
using MediaDownloader.Dto;
using MediaDownloader.Exceptions;
using MediaDownloader.Services;
using Microsoft.Extensions.Options;

namespace MediaDownloader.Telegram;

// проверяем личное сообщение и whitelist, превращаем текст ссылок в задание очереди
public class TelegramUpdateHandler {
    private readonly TelegramOptions options; // whitelist из раздела Telegram
    private readonly TelegramApiClient api; // ответы пользователю через Bot API
    private readonly TelegramDownloadQueue queue; // передача заданий воркеру
    private readonly MediaService mediaService; // общая проверка ссылок
    private readonly ILogger<TelegramUpdateHandler> logger; // причины отклонения обновлений

    // конструктор: сохраняем настройки доступа, API, очередь и сервис проверки
    public TelegramUpdateHandler(IOptions<TelegramOptions> options, TelegramApiClient api,
        TelegramDownloadQueue queue, MediaService mediaService, ILogger<TelegramUpdateHandler> logger) {
        this.options = options.Value;
        this.api = api;
        this.queue = queue;
        this.mediaService = mediaService;
        this.logger = logger;
    }

    // допускаем личное сообщение разрешенного автора, проверяем ссылки и ставим их в очередь
    public async Task handleAsync(TelegramUpdate update, CancellationToken cancellationToken) {
        // из обновления берем сообщение; события без личного чата с положительным ID пропускаем
        TelegramMessage? message = update.Message;
        if (message?.Chat is null || message.Chat.Type != "private" || message.Chat.Id <= 0) {
            return;
        }
        try {
            // проверяем числовой ID автора по AllowedUserIds до разбора текста
            if (message.From is null || !options.AllowedUserIds.Contains(message.From.Id)) {
                throw new AccessDeniedException();
            }
            if (string.IsNullOrWhiteSpace(message.Text)) {
                throw new BadRequestException("Пришлите только HTTP/HTTPS-ссылки через пробелы или переносы строк.");
            }
            // делим текст по пробельным символам и получаем MediaRequest, затем проверяем его общим сервисом
            MediaRequest request = new MediaRequest {
                Urls = message.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToList()
            };
            request = new MediaRequest { Urls = mediaService.validateRequest(request) };
            // вызываем virtual-метод на текущем объекте наследника: прокси запишет ссылки перед очередью
            if (!await enqueueAsync(request, message.Chat.Id, cancellationToken)) {
                await notifyAsync(message.Chat.Id, "Очередь заполнена. Повторите запрос позже.", cancellationToken);
            }
        }
        // ожидаемые ошибки доступа и входа возвращаем сообщением; отмену передаем выше
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

    // передаем проверенный запрос воркеру; SaveLinks добавляет запись истории перед этим действием
    [SaveLinks]
    protected virtual Task<bool> enqueueAsync(MediaRequest request, long chatId, CancellationToken cancellationToken) {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(queue.tryEnqueue(new TelegramDownloadJob { ChatId = chatId, Request = request }));
    }

    // отправляем текст ошибки; при ограничении API выдерживаем указанную задержку
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
