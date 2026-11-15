namespace MediaDownloader.Telegram;

public sealed class TelegramApiException : Exception {
    public string Code { get; }
    public int? RetryAfterSeconds { get; }

    public TelegramApiException(string message, string code, int? retryAfterSeconds = null) : base(message) {
        Code = code;
        RetryAfterSeconds = retryAfterSeconds;
    }
}
