namespace MediaDownloader.Dto;

// DTO запроса на скачивание
public sealed class MediaRequest {
    public List<string>? Urls { get; init; } // исходный список ссылок, порядок и повторы значимы
}
