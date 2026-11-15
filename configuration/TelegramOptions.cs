namespace MediaDownloader.Configuration;

public sealed class TelegramOptions {
    public const string SectionName = "Telegram";

    public bool Enabled { get; set; }
    public string BotToken { get; set; } = string.Empty;
    public List<long> AllowedUserIds { get; set; } = new List<long>();
    public string BaseUrl { get; set; } = "https://api.telegram.org";
    public bool UseLocalBotApi { get; set; }
    public long MaxUploadSizeBytes { get; set; } = 50_000_000;
    public int QueueCapacity { get; set; } = 20;
    public int PollingTimeoutSeconds { get; set; } = 30;
    public int RetryDelaySeconds { get; set; } = 3;
    public int UploadTimeoutSeconds { get; set; } = 1800;
}
