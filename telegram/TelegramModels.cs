using System.Text.Json.Serialization;
using MediaDownloader.Dto;

namespace MediaDownloader.Telegram;

public sealed class TelegramUpdate {
    [JsonPropertyName("update_id")]
    public long? UpdateId { get; init; }
    [JsonPropertyName("message")]
    public TelegramMessage? Message { get; init; }
}

public sealed class TelegramMessage {
    [JsonPropertyName("message_id")]
    public long MessageId { get; init; }
    [JsonPropertyName("chat")]
    public TelegramChat? Chat { get; init; }
    [JsonPropertyName("from")]
    public TelegramUser? From { get; init; }
    [JsonPropertyName("text")]
    public string? Text { get; init; }
}

public sealed class TelegramChat {
    [JsonPropertyName("id")]
    public long Id { get; init; }
    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;
}

public sealed class TelegramUser {
    [JsonPropertyName("id")]
    public long Id { get; init; }
}

public sealed class TelegramDownloadJob {
    public long ChatId { get; init; }
    public MediaRequest Request { get; init; } = new MediaRequest();
}

internal sealed class TelegramEnvelope<T> where T : class {
    [JsonPropertyName("ok")]
    public bool? Ok { get; init; }
    [JsonPropertyName("result")]
    public T? Result { get; init; }
    [JsonPropertyName("error_code")]
    public int? ErrorCode { get; init; }
    [JsonPropertyName("parameters")]
    public TelegramResponseParameters? Parameters { get; init; }
}

internal sealed class TelegramResponseParameters {
    [JsonPropertyName("retry_after")]
    public int? RetryAfter { get; init; }
}
