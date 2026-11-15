namespace MediaDownloader.Telegram;

// ошибка обмена с Bot API с кодом и необязательной задержкой
public sealed class TelegramApiException : Exception {
    public string Code { get; } // причина отказа для обработки и логов
    public int? RetryAfterSeconds { get; } // задержка API в секундах, если задана

    // конструктор: сохраняем сообщение, код и время ожидания
    public TelegramApiException(string message, string code, int? retryAfterSeconds = null) : base(message) {
        Code = code;
        RetryAfterSeconds = retryAfterSeconds;
    }
}
