namespace MediaDownloader.Configuration;

// настройки скачивания из раздела Media
public sealed class MediaOptions {
    public const string SectionName = "Media"; // имя раздела конфигурации

    public int MaxUrlsPerBatch { get; set; } = 10; // максимум ссылок в одном запросе
    public int MaxFilesPerUrl { get; set; } = 100; // максимум файлов одной ссылки
    public int MaxConcurrentOperations { get; set; } = 1; // число одновременно скачиваемых запросов
    public string WorkingRoot { get; set; } = "data/work"; // корневая папка временных заданий
    public long MaxFileSizeBytes { get; set; } = 2_000_000_000; // предел размера одного файла в байтах
    public bool AllowLocalUrls { get; set; } // допуск локальных адресов при проверке входных ссылок
}
