namespace MediaDownloader.Dto;

// DTO результата одной ссылки: файлы либо ошибка
public sealed class MediaResult {
    public int Index { get; init; } // индекс ссылки во входном списке от 0
    public string Url { get; init; } = string.Empty; // исходная строка ссылки
    public bool Success { get; init; } // получены проверенные файлы
    public List<MediaFileResult> Files { get; init; } = new List<MediaFileResult>(); // описания файлов в порядке источника
    public string? ErrorCode { get; init; } // код ошибки этой ссылки
    public string? ErrorMessage { get; init; } // сообщение для пользователя
}
