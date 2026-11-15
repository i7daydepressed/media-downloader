using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MediaDownloader.Dto;
using Microsoft.Extensions.Options;
using MvcJsonOptions = Microsoft.AspNetCore.Mvc.JsonOptions;

namespace MediaDownloader.Tools;

public sealed class MediaArchiveWriter {
    private const string ArchiveFileName = "media.zip";
    private readonly WorkingDirectoryManager directories;
    private readonly JsonSerializerOptions jsonOptions;

    public MediaArchiveWriter(WorkingDirectoryManager directories, IOptions<MvcJsonOptions> jsonOptions) {
        this.directories = directories;
        this.jsonOptions = jsonOptions.Value.JsonSerializerOptions;
    }

    public async Task<string> createArchiveAsync(MediaBatchResult batch, CancellationToken cancellationToken) {
        cancellationToken.ThrowIfCancellationRequested();
        string directory = directories.validateJobDirectory(batch.WorkingDirectory
            ?? throw new InvalidOperationException("У результата отсутствует рабочий каталог."));
        string archivePath = Path.Combine(directory, ArchiveFileName);
        HashSet<string> entryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "results.json" };
        await using (FileStream output = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write,
            FileShare.None, 65_536, FileOptions.Asynchronous | FileOptions.SequentialScan)) {
            using ZipArchive archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true, Encoding.UTF8);
            foreach (MediaResult result in batch.Results) {
                if (!result.Success) {
                    continue;
                }
                foreach (MediaFileResult file in result.Files) {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!Regex.IsMatch(file.FileName, "^[a-zA-Z0-9][a-zA-Z0-9._-]{0,180}$") || !entryNames.Add(file.FileName)) {
                        throw new InvalidDataException("Неверное или повторяющееся имя файла архива.");
                    }
                    string filePath = directories.validateFilePath(directory, file.FilePath
                        ?? throw new InvalidDataException("У медиафайла отсутствует рабочий путь."));
                    if (filePath.Equals(archivePath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) {
                        throw new InvalidDataException("Архив не может включать сам себя.");
                    }
                    ZipArchiveEntry entry = archive.CreateEntry(file.FileName, CompressionLevel.NoCompression);
                    await using Stream entryStream = entry.Open();
                    await using FileStream input = new FileStream(filePath, FileMode.Open, FileAccess.Read,
                        FileShare.Read, 65_536, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    await input.CopyToAsync(entryStream, 65_536, cancellationToken);
                }
            }
            ZipArchiveEntry manifest = archive.CreateEntry("results.json", CompressionLevel.NoCompression);
            await using Stream manifestStream = manifest.Open();
            await JsonSerializer.SerializeAsync(manifestStream, batch, jsonOptions, cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return validateArchivePath(batch, archivePath);
    }

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
