namespace MediaDownloader.Configuration;

// настройки Telegram-адаптера из раздела Telegram
public sealed class TelegramOptions {
    public const string SectionName = "Telegram"; // имя раздела конфигурации

    public bool Enabled { get; set; } // запуск polling и воркера
    public string BotToken { get; set; } = string.Empty; // токен доступа к Bot API
    public List<long> AllowedUserIds { get; set; } = new List<long>(); // разрешенные числовые ID отправителей
    public string BaseUrl { get; set; } = "https://api.telegram.org"; // адрес облачного или локального Bot API
    public bool UseLocalBotApi { get; set; } // локальный режим с отдельным пределом загрузки
    public long MaxUploadSizeBytes { get; set; } = 50_000_000; // максимум байтов отправляемого файла
    public int QueueCapacity { get; set; } = 20; // число ожидающих заданий
    public int PollingTimeoutSeconds { get; set; } = 30; // ожидание сообщений в одном getUpdates, секунды
    public int RetryDelaySeconds { get; set; } = 3; // задержка повторного опроса при ошибке, секунды
    public int UploadTimeoutSeconds { get; set; } = 1800; // время одной отправки файла, секунды
    public string LinksHistoryPath { get; set; } = "data/history/telegram-links.txt"; // постоянная история даты и URL
}
