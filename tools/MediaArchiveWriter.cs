using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MediaDownloader.Dto;
using Microsoft.Extensions.Options;
using MvcJsonOptions = Microsoft.AspNetCore.Mvc.JsonOptions;

namespace MediaDownloader.Tools;

// собираем временный ZIP из файлов результата и добавляем JSON с исходом каждой ссылки
public sealed class MediaArchiveWriter {
    private const string ArchiveFileName = "media.zip"; // имя ZIP внутри папки задания
    private readonly WorkingDirectoryManager directories; // проверка папки и путей файлов
    private readonly JsonSerializerOptions jsonOptions; // те же правила JSON, что у HTTP-контроллеров

    // конструктор: сохраняем службу каталогов и правила сериализации MVC
    public MediaArchiveWriter(WorkingDirectoryManager directories, IOptions<MvcJsonOptions> jsonOptions) {
        this.directories = directories;
        this.jsonOptions = jsonOptions.Value.JsonSerializerOptions;
    }

    // создаем ZIP в папке batch, записываем файлы и results.json, возвращаем проверенный полный путь
    public async Task<string> createArchiveAsync(MediaBatchResult batch, CancellationToken cancellationToken) {
        cancellationToken.ThrowIfCancellationRequested();
        // проверяем принадлежность папки заданию и резервируем имя results.json среди записей архива
        string directory = directories.validateJobDirectory(batch.WorkingDirectory
            ?? throw new InvalidOperationException("У результата отсутствует рабочий каталог."));
        string archivePath = Path.Combine(directory, ArchiveFileName);
        HashSet<string> entryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "results.json" };
        // создаем новый ZIP без перезаписи, включаем только файлы успешных ссылок
        await using (FileStream output = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write,
            FileShare.None, 65_536, FileOptions.Asynchronous | FileOptions.SequentialScan)) {
            using ZipArchive archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true, Encoding.UTF8);
            foreach (MediaResult result in batch.Results) {
                if (!result.Success) {
                    continue;
                }
                foreach (MediaFileResult file in result.Files) {
                    cancellationToken.ThrowIfCancellationRequested();
                    // проверяем безопасное уникальное имя записи, затем полный путь файла внутри задания
                    if (!Regex.IsMatch(file.FileName, "^[a-zA-Z0-9][a-zA-Z0-9._-]{0,180}$") || !entryNames.Add(file.FileName)) {
                        throw new InvalidDataException("Неверное или повторяющееся имя файла архива.");
                    }
                    string filePath = directories.validateFilePath(directory, file.FilePath
                        ?? throw new InvalidDataException("У медиафайла отсутствует рабочий путь."));
                    if (filePath.Equals(archivePath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) {
                        throw new InvalidDataException("Архив не может включать сам себя.");
                    }
                    // добавляем файл без повторного сжатия и копируем его поток в запись ZIP
                    ZipArchiveEntry entry = archive.CreateEntry(file.FileName, CompressionLevel.NoCompression);
                    await using Stream entryStream = entry.Open();
                    await using FileStream input = new FileStream(filePath, FileMode.Open, FileAccess.Read,
                        FileShare.Read, 65_536, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    await input.CopyToAsync(entryStream, 65_536, cancellationToken);
                }
            }
            // сериализуем весь batch в results.json; служебные пути исключены атрибутами DTO
            ZipArchiveEntry manifest = archive.CreateEntry("results.json", CompressionLevel.NoCompression);
            await using Stream manifestStream = manifest.Open();
            await JsonSerializer.SerializeAsync(manifestStream, batch, jsonOptions, cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return validateArchivePath(batch, archivePath);
    }

    // допускаем только media.zip в проверенной папке задания, затем проверяем сам файл
    public string validateArchivePath(MediaBatchResult batch, string archivePath) {
        string directory = directories.validateJobDirectory(batch.WorkingDirectory
            ?? throw new InvalidDataException("У результата отсутствует рабочий каталог."));
        string expected = Path.Combine(directory, ArchiveFileName);
        if (!Path.GetFullPath(archivePath).Equals(expected,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) {
            throw new InvalidDataException("Неверный путь временного архива.");
        }
        return directories.validateFilePath(directory, archivePath);
    }
}
