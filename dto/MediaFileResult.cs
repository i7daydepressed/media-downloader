using System.Text.Json.Serialization;

namespace MediaDownloader.Dto;

// DTO одного файла: имя выдачи, характеристики и внутренний путь
public sealed class MediaFileResult {
    public int Index { get; init; } // индекс файла внутри результата ссылки от 0
    public string FileName { get; init; } = string.Empty; // имя для ZIP или отправки, не имя на диске
    public long SizeBytes { get; init; } // размер в байтах
    public double? DurationSeconds { get; init; } // длительность в секундах, если известна
    public int? Width { get; init; } // ширина кадра в пикселях, если известна
    public int? Height { get; init; } // высота кадра в пикселях, если известна

    [JsonIgnore]
    public string? FilePath { get; init; } // полный путь файла на диске, исключен из JSON

    [JsonIgnore]
    public bool IsCompatibleVideo { get; init; } // пригодность для отправки через sendVideo, исключена из JSON
}
