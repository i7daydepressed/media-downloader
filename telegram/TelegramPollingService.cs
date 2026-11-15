using MediaDownloader.Configuration;
using Microsoft.Extensions.Options;

namespace MediaDownloader.Telegram;

public sealed class TelegramPollingService : BackgroundService {
    private readonly TelegramOptions options;
    private readonly TelegramApiClient api;
    private readonly TelegramUpdateHandler handler;
    private readonly IHostApplicationLifetime lifetime;
    private readonly ILogger<TelegramPollingService> logger;

    public TelegramPollingService(IOptions<TelegramOptions> options, TelegramApiClient api,
        TelegramUpdateHandler handler, IHostApplicationLifetime lifetime, ILogger<TelegramPollingService> logger) {
        this.options = options.Value;
        this.api = api;
        this.handler = handler;
        this.lifetime = lifetime;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        if (!options.Enabled) {
            return;
        }
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, lifetime.ApplicationStopping);
        long offset = 0;
        while (!linked.IsCancellationRequested) {
            try {
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
                    offset = id + 1;
                }
                if (updates.Count == 0) {
                    await Task.Delay(100, linked.Token);
                }
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested) {
                break;
            }
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

    private static async Task delayRetryAsync(int seconds, CancellationToken cancellationToken) {
        try {
            await TelegramApiClient.waitRetryAfterAsync(seconds, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }
}
