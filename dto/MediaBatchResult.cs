using System.Text.Json.Serialization;

namespace MediaDownloader.Dto;

// DTO всего скачивания: результаты ссылок и служебная папка файлов
public sealed class MediaBatchResult {
    public List<MediaResult> Results { get; init; } = new List<MediaResult>(); // результат каждой ссылки во входном порядке

    [JsonIgnore]
    public string? WorkingDirectory { get; init; } // полный путь папки задания, исключен из JSON
}
