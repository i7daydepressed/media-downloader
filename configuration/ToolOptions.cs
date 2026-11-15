namespace MediaDownloader.Configuration;

// пути и ограничения внешних утилит из раздела Tools
public sealed class ToolOptions {
    public const string SectionName = "Tools"; // имя раздела конфигурации

    public string YtDlpPath { get; set; } = "yt-dlp"; // путь или имя yt-dlp в PATH
    public string GalleryDlPath { get; set; } = "gallery-dl"; // путь или имя gallery-dl в PATH
    public string FfmpegPath { get; set; } = "ffmpeg"; // исполняемый FFmpeg или папка с ним
    public string FfprobePath { get; set; } = "ffprobe"; // путь или имя FFprobe в PATH
    public string JavaScriptRuntime { get; set; } = "deno"; // имя JS-среды для yt-dlp
    public string JavaScriptRuntimePath { get; set; } = "deno"; // путь к исполняемому файлу JS-среды
    public int ProcessTimeoutSeconds { get; set; } = 600; // время работы одного процесса, секунды
    public int MaxCapturedOutputCharacters { get; set; } = 65_536; // предел сохраняемого текста каждого потока процесса
}
