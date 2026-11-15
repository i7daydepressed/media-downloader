using MediaDownloader.Configuration;
using MediaDownloader.Dto;
using MediaDownloader.Exceptions;
using MediaDownloader.Tools;
using Microsoft.Extensions.Options;

namespace MediaDownloader.Services;

// сервис подготовки скачанных видео: пробуем перепаковать подходящий MKV в MP4 без перекодирования
public sealed class MediaPreparationService {
    private readonly MediaOptions options; // ограничение размера готового файла
    private readonly FfmpegClient ffmpeg; // копирует медиадорожки в другой контейнер через FFmpeg
    private readonly FfprobeClient ffprobe; // читает характеристики медиафайла
    private readonly WorkingDirectoryManager directories; // проверяет пути, создает папки и удаляет рабочие файлы
    private readonly ILogger<MediaPreparationService> logger; // причины возврата исходного файла в логах

    // конструктор: сохраняем настройки и службы для подготовки видео
    public MediaPreparationService(IOptions<MediaOptions> options, FfmpegClient ffmpeg, FfprobeClient ffprobe,
        WorkingDirectoryManager directories, ILogger<MediaPreparationService> logger) {
        // Program.addValidatedOptions привязывает раздел Media из конфигурации к MediaOptions
        this.options = options.Value; // настройки Media из appsettings.json и переопределений конфигурации
        this.ffmpeg = ffmpeg;
        this.ffprobe = ffprobe;
        this.directories = directories;
        this.logger = logger;
    }

    // готовим файлы по списку DTO MediaFileResult и возвращаем обновленные описания в том же порядке
    public async Task<List<MediaFileResult>> prepareAsync(string jobDirectory, List<MediaFileResult> files,
        CancellationToken cancellationToken) {
        List<MediaFileResult> prepared = new List<MediaFileResult>(files.Count); // обновленные описания файлов
        // jobDirectory содержит полный путь папки скачивания, files — описания файлов с полными FilePath
        // по очереди передаем их в prepareFileAsync и добавляем возвращенные DTO в prepared
        foreach (MediaFileResult file in files) {
            // перед следующим файлом проверяем отмену подготовки
            cancellationToken.ThrowIfCancellationRequested();
            prepared.Add(await prepareFileAsync(jobDirectory, file, cancellationToken));
        }
        return prepared;
    }

    // пробуем заменить подходящий MKV проверенным MP4 без перекодирования
    private async Task<MediaFileResult> prepareFileAsync(string jobDirectory, MediaFileResult original,
        CancellationToken cancellationToken) {
        // готовим только файлы с путем и расширением .mkv; остальные описания возвращаем без изменений
        if (original.FilePath is null || !Path.GetExtension(original.FilePath).Equals(".mkv", StringComparison.OrdinalIgnoreCase)) {
            return original;
        }
        string? candidateDirectory = null; // папка нового MP4, пока не создана
        bool accepted = false; // принят ли MP4 вместо исходника
        try {
            string sourcePath = directories.validateFilePath(jobDirectory, original.FilePath); // проверенный полный путь исходника в папке задания
            MediaProbeResult source = await ffprobe.probeAsync(sourcePath, Path.GetDirectoryName(sourcePath)!, cancellationToken); // характеристики исходника
            // для копирования дорожек в MP4 проверяем контейнер Matroska и совместимость потоков через FFprobe
            // если проверка не пройдена, возвращаем исходное описание original
            if (!source.HasCompatibleMp4Streams || source.ContainerFormat?.Split(',').Contains("matroska", StringComparer.Ordinal) != true) {
                return original;
            }
            // создаем рядом с исходником папку prepared-<id> и путь нового файла media.mp4 в ней
            candidateDirectory = directories.createPreparationDirectory(jobDirectory, sourcePath);
            string candidatePath = Path.Combine(candidateDirectory, "media.mp4");
            // передаем оба пути в ffmpeg.remuxToMp4Async: FFmpeg копирует дорожки из MKV в новый MP4
            // исходный файл пока остается на диске по sourcePath
            await ffmpeg.remuxToMp4Async(sourcePath, candidatePath, cancellationToken);
            // проверяем, что созданный MP4 существует в папке кандидата и путь не содержит символических ссылок
            candidatePath = directories.validateFilePath(jobDirectory, candidatePath, candidateDirectory);
            long size = new FileInfo(candidatePath).Length; // размер нового MP4 в байтах
            // пустой файл или превышение MaxFileSizeBytes превращаем в ошибку подготовки
            if (size == 0 || size > options.MaxFileSizeBytes) {
                throw invalidPreparedMedia();
            }
            MediaProbeResult candidate = await ffprobe.probeAsync(candidatePath, candidateDirectory, cancellationToken); // характеристики нового MP4
            // подтверждаем совместимость нового MP4 и сравниваем его характеристики с исходником через preservesMedia
            if (!candidate.IsCompatibleVideo || !candidate.HasCompatibleMp4Streams || !preservesMedia(source, candidate)) {
                throw invalidPreparedMedia();
            }
            // собираем новый DTO: индекс сохраняем, расширение имени выдачи меняем на .mp4
            // FilePath указывает на MP4, размер и характеристики берем из проверенного результата
            MediaFileResult prepared = new MediaFileResult {
                Index = original.Index, FileName = Path.ChangeExtension(original.FileName, ".mp4"), SizeBytes = size,
                DurationSeconds = candidate.DurationSeconds, Width = candidate.Width, Height = candidate.Height,
                FilePath = candidatePath, IsCompatibleVideo = true
            };
            // учитываем отмену перед удалением исходника
            cancellationToken.ThrowIfCancellationRequested();
            // удаляем исходник только после проверки MP4; при отказе возвращаем original
            if (!await directories.deleteOwnedFileAsync(jobDirectory, sourcePath)) {
                return original;
            }
            accepted = true; // оставляем папку MP4: finally больше не будет ее очищать
            return prepared;
        }
        catch (Exception exception) when (exception is ExternalToolException or IOException or InvalidDataException or
            UnauthorizedAccessException or ArgumentException or NotSupportedException) {
            // при перечисленных ошибках записываем код инструмента или тип исключения и возвращаем original
            // отмена не входит в фильтр catch и прерывает подготовку
            logger.LogWarning("Media preparation retains original after {Code}.",
                exception is ExternalToolException tool ? tool.Code : exception.GetType().Name);
            return original;
        }
        finally {
            // если MP4 не принят, пробуем удалить папку кандидата вместе с ее содержимым
            if (!accepted && candidateDirectory is not null) {
                await directories.cleanupAttemptAsync(jobDirectory, candidateDirectory);
            }
        }
    }

    // сравниваем кодеки, формат пикселей, размеры кадра и длительность исходника и нового MP4
    private static bool preservesMedia(MediaProbeResult source, MediaProbeResult candidate) {
        // для длительности допускаем погрешность 1% исходной, не меньше 0,1 с и не больше 0,5 с
        return source.VideoCodec == candidate.VideoCodec && source.AudioCodec == candidate.AudioCodec &&
            source.PixelFormat == candidate.PixelFormat && source.Width == candidate.Width && source.Height == candidate.Height &&
            source.DurationSeconds is double before && candidate.DurationSeconds is double after &&
            Math.Abs(before - after) <= Math.Max(0.1, Math.Min(0.5, before * 0.01));
    }

    // создаем ошибку invalid_prepared_media, которую prepareFileAsync перехватит для возврата исходного файла
    private static ExternalToolException invalidPreparedMedia() {
        return new ExternalToolException("Prepared media did not preserve the source properties.", "invalid_prepared_media");
    }
}
