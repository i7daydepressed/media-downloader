using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using MediaDownloader.Configuration;
using Microsoft.Extensions.Options;

namespace MediaDownloader.Telegram;

public sealed class TelegramApiClient : IDisposable {
    private const int MaxResponseBytes = 1_048_576;
    private readonly HttpClient httpClient;
    private readonly TelegramOptions options;
    private readonly ILogger<TelegramApiClient> logger;

    public TelegramApiClient(HttpClient httpClient, IOptions<TelegramOptions> options, ILogger<TelegramApiClient> logger) {
        this.httpClient = httpClient;
        this.httpClient.Timeout = Timeout.InfiniteTimeSpan;
        this.options = options.Value;
        this.logger = logger;
    }

    public async Task<List<TelegramUpdate>> getUpdatesAsync(long offset, CancellationToken cancellationToken) {
        using JsonContent content = JsonContent.Create(new {
            offset,
            timeout = options.PollingTimeoutSeconds,
            limit = 1,
            allowed_updates = new[] { "message" }
        });
        List<TelegramUpdate> updates = await sendAsync<List<TelegramUpdate>>("getUpdates", content,
            TimeSpan.FromSeconds(options.PollingTimeoutSeconds + 10), cancellationToken);
        if (updates.Any(update => update is null || update.UpdateId is null or < 0 or long.MaxValue)) {
            throw invalidResponse();
        }
        return updates;
    }

    public async Task sendMessageAsync(long chatId, string text, CancellationToken cancellationToken) {
        using JsonContent content = JsonContent.Create(new { chat_id = chatId, text });
        TelegramMessage message = await sendAsync<TelegramMessage>("sendMessage", content,
            TimeSpan.FromSeconds(30), cancellationToken);
        if (message.MessageId <= 0) {
            throw invalidResponse();
        }
    }

    public Task sendDocumentAsync(long chatId, string filePath, string fileName, CancellationToken cancellationToken) {
        return sendFileAsync(chatId, filePath, fileName, false, null, null, null, cancellationToken);
    }

    public Task sendVideoAsync(long chatId, string filePath, string fileName, double? durationSeconds,
        int? width, int? height, CancellationToken cancellationToken) {
        return sendFileAsync(chatId, filePath, fileName, true, durationSeconds, width, height, cancellationToken);
    }

    private async Task sendFileAsync(long chatId, string filePath, string fileName, bool video,
        double? durationSeconds, int? width, int? height, CancellationToken cancellationToken) {
        for (int attempt = 0; ; attempt++) {
            try {
                await sendFileAttemptAsync(chatId, filePath, fileName, video, durationSeconds, width, height, cancellationToken);
                return;
            }
            catch (TelegramApiException exception) when (exception.Code == "telegram_rate_limited" &&
                exception.RetryAfterSeconds is not null && attempt < 2) {
                logger.LogWarning("Telegram upload rate limited; retry {Attempt} after {Seconds} seconds.",
                    attempt + 1, exception.RetryAfterSeconds.Value);
                await waitRetryAfterAsync(exception.RetryAfterSeconds.Value, cancellationToken);
            }
        }
    }

    private async Task sendFileAttemptAsync(long chatId, string filePath, string fileName, bool video,
        double? durationSeconds, int? width, int? height, CancellationToken cancellationToken) {
        try {
            await using FileStream input = new FileStream(filePath, FileMode.Open, FileAccess.Read,
                FileShare.Read, 65_536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using MultipartFormDataContent content = new MultipartFormDataContent();
            content.Add(new StringContent(chatId.ToString(CultureInfo.InvariantCulture)), "chat_id");
            StreamContent file = new StreamContent(input, 65_536);
            file.Headers.ContentType = new MediaTypeHeaderValue(video ? "video/mp4" : "application/octet-stream");
            content.Add(file, video ? "video" : "document", fileName);
            if (video) {
                if (durationSeconds is double duration && double.IsFinite(duration) && duration > 0 && duration <= int.MaxValue) {
                    content.Add(new StringContent(Math.Ceiling(duration).ToString(CultureInfo.InvariantCulture)), "duration");
                }
                if (width is > 0) {
                    content.Add(new StringContent(width.Value.ToString(CultureInfo.InvariantCulture)), "width");
                }
                if (height is > 0) {
                    content.Add(new StringContent(height.Value.ToString(CultureInfo.InvariantCulture)), "height");
                }
            }
            TelegramMessage message = await sendAsync<TelegramMessage>(video ? "sendVideo" : "sendDocument", content,
                TimeSpan.FromSeconds(options.UploadTimeoutSeconds), cancellationToken);
            if (message.MessageId <= 0) {
                throw invalidResponse();
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
            throw new TelegramApiException("Не удалось прочитать файл для отправки.", "telegram_file_error");
        }
    }

    private async Task<T> sendAsync<T>(string method, HttpContent content, TimeSpan timeout,
        CancellationToken cancellationToken) where T : class {
        cancellationToken.ThrowIfCancellationRequested();
        using CancellationTokenSource deadline = new CancellationTokenSource(timeout);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try {
            // У клиента нет HttpClientFactory logging handlers; endpoint с token нигде не журналируется.
            Uri endpoint = new Uri(options.BaseUrl.TrimEnd('/') + "/bot" + options.BotToken + "/" + method);
            using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = content };
            using HttpResponseMessage response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token);
            await using Stream input = await response.Content.ReadAsStreamAsync(linked.Token);
            using MemoryStream body = new MemoryStream();
            byte[] buffer = new byte[8192];
            int count;
            while ((count = await input.ReadAsync(buffer.AsMemory(), linked.Token)) > 0) {
                if (body.Length + count > MaxResponseBytes) {
                    throw invalidResponse();
                }
                body.Write(buffer, 0, count);
            }
            TelegramEnvelope<T>? envelope = JsonSerializer.Deserialize<TelegramEnvelope<T>>(
                body.GetBuffer().AsSpan(0, (int)body.Length));
            if (envelope?.Ok is null) {
                throw invalidResponse();
            }
            if (!response.IsSuccessStatusCode || envelope.Ok != true) {
                int error = envelope.ErrorCode ?? (int)response.StatusCode;
                int? delay = error == 429 ? envelope.Parameters?.RetryAfter is > 0
                    ? envelope.Parameters.RetryAfter : options.RetryDelaySeconds : null;
                throw new TelegramApiException(error == 429 ? "Telegram временно ограничил частоту запросов."
                    : "Telegram API не подтвердил операцию.", error == 429 ? "telegram_rate_limited" : "telegram_api_error", delay);
            }
            return envelope.Result ?? throw invalidResponse();
        }
        catch (OperationCanceledException) {
            cancellationToken.ThrowIfCancellationRequested();
            throw new TelegramApiException("Превышено время запроса Telegram API.", "telegram_timeout");
        }
        catch (JsonException) {
            throw invalidResponse();
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException) {
            // Не прикрепляем исходное исключение: оно может содержать token URL.
            throw new TelegramApiException("Не удалось связаться с Telegram API.", "telegram_transport_error");
        }
    }

    private static TelegramApiException invalidResponse() {
        return new TelegramApiException("Telegram API вернул некорректный ответ.", "telegram_invalid_response");
    }

    public static async Task waitRetryAfterAsync(int seconds, CancellationToken cancellationToken) {
        while (seconds > 0) {
            int part = Math.Min(seconds, 60);
            await Task.Delay(TimeSpan.FromSeconds(part), cancellationToken);
            seconds -= part;
        }
    }

    public void Dispose() {
        httpClient.Dispose();
    }
}
