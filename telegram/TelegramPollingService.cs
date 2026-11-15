using MediaDownloader.Configuration;
using Microsoft.Extensions.Options;

namespace MediaDownloader.Telegram;

// фоновая служба long polling: получаем сообщения, передаем обработчику и продвигаем offset
public sealed class TelegramPollingService : BackgroundService {
    private readonly TelegramOptions options; // включение polling и интервалы повторов
    private readonly TelegramApiClient api; // запрос getUpdates
    private readonly TelegramUpdateHandler handler; // проверка сообщения и запись задания в очередь
    private readonly IHostApplicationLifetime lifetime; // остановка опроса при завершении сервера
    private readonly ILogger<TelegramPollingService> logger; // ошибки опроса и обработки обновлений

    // конструктор: сохраняем настройки и зависимости опроса
    public TelegramPollingService(IOptions<TelegramOptions> options, TelegramApiClient api,
        TelegramUpdateHandler handler, IHostApplicationLifetime lifetime, ILogger<TelegramPollingService> logger) {
        this.options = options.Value;
        this.api = api;
        this.handler = handler;
        this.lifetime = lifetime;
        this.logger = logger;
    }

    // при включенном адаптере опрашиваем Bot API до отмены фоновой службы или остановки сервера
    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        if (!options.Enabled) {
            return;
        }
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, lifetime.ApplicationStopping);
        long offset = 0; // минимальный ID следующего обновления, хранится только в памяти
        while (!linked.IsCancellationRequested) {
            try {
                // получаем обновления начиная с offset и обрабатываем их по возрастанию ID
                List<TelegramUpdate> updates = await api.getUpdatesAsync(offset, linked.Token);
                foreach (TelegramUpdate update in updates.OrderBy(item => item.UpdateId)) {
                    long id = update.UpdateId!.Value;
                    if (id < offset) {
                        continue;
                    }
                    try {
                        await handler.handleAsync(update, linked.Token);
                    }
                    catch (OperationCanceledException) when (linked.IsCancellationRequested) {
                        throw;
                    }
                    catch (Exception exception) {
                        logger.LogError("Telegram update explicitly rejected after {ExceptionType}.", exception.GetType().Name);
                    }
                    // после обработки или зафиксированного отказа переходим к следующему ID; завершения скачивания здесь не ждем
                    offset = id + 1;
                }
                if (updates.Count == 0) {
                    await Task.Delay(100, linked.Token);
                }
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested) {
                break;
            }
            // после ошибки API ждем retry_after либо настроенную задержку и повторяем опрос
            catch (TelegramApiException exception) {
                logger.LogWarning("Telegram polling will retry after {Code}.", exception.Code);
                await delayRetryAsync(exception.RetryAfterSeconds ?? options.RetryDelaySeconds, linked.Token);
            }
            catch (Exception exception) {
                logger.LogError("Telegram polling will retry after {ExceptionType}.", exception.GetType().Name);
                await delayRetryAsync(options.RetryDelaySeconds, linked.Token);
            }
        }
    }

    // ждем перед новым опросом; отмена ожидания завершает паузу без новой ошибки
    private static async Task delayRetryAsync(int seconds, CancellationToken cancellationToken) {
        try {
            await TelegramApiClient.waitRetryAfterAsync(seconds, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }
}
