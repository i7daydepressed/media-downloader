namespace MediaDownloader.Tools;

// характеристики файла из FFprobe для проверки результата и подготовки MP4
public sealed class MediaProbeResult {
    public double? DurationSeconds { get; init; } // длительность в секундах, если указана
    public int? Width { get; init; } // ширина кадра в пикселях
    public int? Height { get; init; } // высота кадра в пикселях
    public string? VideoCodec { get; init; } // кодек первой видеодорожки
    public string? AudioCodec { get; init; } // кодек первой аудиодорожки
    public string? PixelFormat { get; init; } // формат пикселей видео
    public string? ContainerFormat { get; init; } // имена контейнера из format_name
    public bool IsCompatibleVideo { get; init; } // MP4 с допустимыми метаданными, H.264/yuv420p и AAC либо без звука
    public bool HasCompatibleMp4Streams { get; init; } // совместимые дорожки без дополнительных потоков
}
