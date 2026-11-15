using System.Text.Json.Serialization;
using MediaDownloader.Dto;

namespace MediaDownloader.Telegram;

// DTO обновления Telegram: номер для следующего offset и полученное сообщение
public sealed class TelegramUpdate {
    [JsonPropertyName("update_id")]
    public long? UpdateId { get; init; } // номер обновления, следующий опрос использует номер плюс 1
    [JsonPropertyName("message")]
    public TelegramMessage? Message { get; init; }
}

// DTO сообщения: чат, отправитель, текст и идентификатор подтвержденной отправки
public sealed class TelegramMessage {
    [JsonPropertyName("message_id")]
    public long MessageId { get; init; } // положительный ID подтверждает отправку сообщения
    [JsonPropertyName("chat")]
    public TelegramChat? Chat { get; init; } // чат назначения
    [JsonPropertyName("from")]
    public TelegramUser? From { get; init; } // автор сообщения для проверки доступа
    [JsonPropertyName("text")]
    public string? Text { get; init; } // текст со ссылками
}

// DTO чата: числовой ID и тип, используемые при допуске сообщений
public sealed class TelegramChat {
    [JsonPropertyName("id")]
    public long Id { get; init; }
    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty; // для обработки допускается private
}

// DTO отправителя: числовой ID для whitelist
public sealed class TelegramUser {
    [JsonPropertyName("id")]
    public long Id { get; init; }
}

// задание очереди: кому отправить результат и какие ссылки скачать
public sealed class TelegramDownloadJob {
    public long ChatId { get; init; } // куда отправлять файлы и ошибки
    public MediaRequest Request { get; init; } = new MediaRequest(); // проверенный список ссылок
}

// общая оболочка ответа Bot API: успех, данные результата или код ошибки
internal sealed class TelegramEnvelope<T> where T : class {
    [JsonPropertyName("ok")]
    public bool? Ok { get; init; } // подтверждение операции, отсутствие считается неверным ответом
    [JsonPropertyName("result")]
    public T? Result { get; init; } // данные запрошенного метода API
    [JsonPropertyName("error_code")]
    public int? ErrorCode { get; init; } // код API, в том числе 429 для ожидания
    [JsonPropertyName("parameters")]
    public TelegramResponseParameters? Parameters { get; init; }
}

// дополнительные параметры ошибки Bot API
internal sealed class TelegramResponseParameters {
    [JsonPropertyName("retry_after")]
    public int? RetryAfter { get; init; } // сколько секунд выждать перед следующим запросом
}
