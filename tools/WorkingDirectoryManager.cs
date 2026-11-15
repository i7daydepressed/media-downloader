using System.Globalization;
using System.Text.RegularExpressions;
using MediaDownloader.Configuration;
using MediaDownloader.Exceptions;
using Microsoft.Extensions.Options;

namespace MediaDownloader.Tools;

// служба временных файлов: создаем помеченные задания, проверяем владение путями и выполняем очистку
public sealed class WorkingDirectoryManager {
    private const string MarkerFileName = ".media-downloader-job"; // файл подтверждения принадлежности папки заданию
    private const string MarkerPrefix = "media-downloader:0.1.0:"; // префикс содержимого маркера перед ID задания
    private readonly ILogger<WorkingDirectoryManager> logger; // неуспешные попытки очистки
    private readonly StringComparison pathComparison; // сравнение путей с учетом правил текущей ОС

    public string RootDirectory { get; } // полный путь рабочего корня из Media.WorkingRoot

    // конструктор: разрешаем WorkingRoot относительно приложения и выбираем сравнение путей
    public WorkingDirectoryManager(IOptions<MediaOptions> options, IHostEnvironment environment,
        ILogger<WorkingDirectoryManager> logger) {
        RootDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.Value.WorkingRoot, environment.ContentRootPath));
        this.logger = logger;
        pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    }

    // проверяем отсутствие символических ссылок и создаем рабочий корень
    public Task initializeAsync(CancellationToken cancellationToken) {
        cancellationToken.ThrowIfCancellationRequested();
        ensureNoSymbolicLinks(RootDirectory);
        Directory.CreateDirectory(RootDirectory);
        ensureNoSymbolicLinks(RootDirectory);
        return Task.CompletedTask;
    }

    // создаем отдельную папку job-<id> с маркером и возвращаем полный путь
    public string createJobDirectory() {
        ensureNoSymbolicLinks(RootDirectory);
        string id = Guid.NewGuid().ToString("N");
        string directory = Path.Combine(RootDirectory, "job-" + id);
        Directory.CreateDirectory(directory);
        ensureNoSymbolicLinks(directory);
        // записываем версию маркера и ID папки, чтобы очистка могла подтвердить ее принадлежность
        using FileStream marker = new FileStream(Path.Combine(directory, MarkerFileName),
            FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using StreamWriter writer = new StreamWriter(marker);
        writer.Write(MarkerPrefix + id);
        return directory;
    }

    // возвращаем полный путь папки только после проверки ее имени и маркера
    public string validateJobDirectory(string jobDirectory) {
        string directory = Path.GetFullPath(jobDirectory);
        requireOwnedJob(directory);
        return directory;
    }

    // создаем подпапку инструмента внутри номера ссылки после проверки задания и имени попытки
    public string createAttemptDirectory(string jobDirectory, int index, string attemptName) {
        requireOwnedJob(jobDirectory);
        if (index < 0 || !Regex.IsMatch(attemptName, "^[a-z-]+$")) {
            throw new ArgumentException("Неверный идентификатор попытки.");
        }
        string directory = Path.Combine(jobDirectory, index.ToString("D4", CultureInfo.InvariantCulture), attemptName);
        ensureNoSymbolicLinks(directory);
        Directory.CreateDirectory(directory);
        ensureNoSymbolicLinks(directory);
        return directory;
    }

    // проверяем полный путь существующего файла внутри задания и при необходимости внутри конкретной попытки
    public string validateFilePath(string jobDirectory, string filePath, string? attemptDirectory = null) {
        requireOwnedJob(jobDirectory);
        string fullPath;
        try {
            fullPath = Path.GetFullPath(filePath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException) {
            throw invalidOutputPath();
        }
        // сравниваем полный путь с границами каталогов, затем исключаем символические ссылки и отсутствие файла
        if (!isDescendant(jobDirectory, fullPath) ||
            (attemptDirectory is not null && !isDescendant(attemptDirectory, fullPath))) {
            throw invalidOutputPath();
        }
        try {
            ensureNoSymbolicLinks(fullPath);
        }
        catch (InvalidDataException) {
            throw invalidOutputPath();
        }
        if (!File.Exists(fullPath) || Directory.Exists(fullPath)) {
            throw new ExternalToolException("Итоговый медиафайл отсутствует.", "output_missing");
        }
        return fullPath;
    }

    // рядом с проверенным исходником создаем отдельную папку prepared-<id> для нового MP4
    public string createPreparationDirectory(string jobDirectory, string sourcePath) {
        string parent = Path.GetDirectoryName(validateFilePath(jobDirectory, sourcePath))!;
        string directory = Path.Combine(parent, "prepared-" + Guid.NewGuid().ToString("N"));
        ensureNoSymbolicLinks(directory);
        Directory.CreateDirectory(directory);
        ensureNoSymbolicLinks(directory);
        return directory;
    }

    // пытаемся удалить папку одной попытки внутри проверенного задания
    public Task<bool> cleanupAttemptAsync(string jobDirectory, string attemptDirectory) {
        return cleanupAsync(jobDirectory, attemptDirectory, false);
    }

    // пытаемся удалить все задание; при отсутствии пути удалять нечего
    public Task<bool> cleanupJobAsync(string? jobDirectory) {
        if (jobDirectory is null) {
            return Task.FromResult(true);
        }
        return cleanupAsync(jobDirectory, jobDirectory, true);
    }

    // передаем удаление доставленного файла общей проверке владения путем
    public Task<bool> deleteDeliveredFileAsync(string jobDirectory, string filePath) {
        return deleteOwnedFileAsync(jobDirectory, filePath);
    }

    // пробуем удалить один файл своего задания; перехваченная ошибка дает false
    public Task<bool> deleteOwnedFileAsync(string jobDirectory, string filePath) {
        try {
            requireOwnedJob(jobDirectory);
            string fullPath = Path.GetFullPath(filePath);
            if (!isDescendant(jobDirectory, fullPath)) {
                throw invalidOutputPath();
            }
            ensureNoSymbolicLinks(fullPath);
            if (Directory.Exists(fullPath)) {
                throw invalidOutputPath();
            }
            // удаляем проверенный файл; отсутствие файла также считается успешным исходом
            File.Delete(fullPath);
            return Task.FromResult(true);
        }
        catch (Exception exception) when (isCleanupException(exception)) {
            logCleanupFailure(exception);
            return Task.FromResult(false);
        }
    }

    // при запуске перебираем рабочий корень и удаляем только папки с собственным маркером
    public async Task cleanupStaleJobsAsync(CancellationToken cancellationToken) {
        ensureNoSymbolicLinks(RootDirectory);
        foreach (FileSystemInfo entry in new DirectoryInfo(RootDirectory).EnumerateFileSystemInfos()) {
            cancellationToken.ThrowIfCancellationRequested();
            if ((entry.Attributes & FileAttributes.Directory) == 0 ||
                (entry.Attributes & FileAttributes.ReparsePoint) != 0 || !isOwnedJob(entry.FullName)) {
                continue;
            }
            await cleanupJobAsync(entry.FullName);
        }
    }

    // проверяем принадлежность цели и пробуем удалить дерево, ожидаемые ошибки записываем и возвращаем false
    private Task<bool> cleanupAsync(string jobDirectory, string target, bool wholeJob) {
        try {
            string fullJob = Path.GetFullPath(jobDirectory);
            // отсутствующее задание с правильным путем и именем считаем уже очищенным
            if (wholeJob && isJobName(fullJob) && !Directory.Exists(fullJob) && !File.Exists(fullJob)) {
                ensureNoSymbolicLinks(fullJob);
                return Task.FromResult(true);
            }
            // для существующего задания проверяем маркер и границы удаляемой цели
            requireOwnedJob(fullJob);
            string fullTarget = Path.GetFullPath(target);
            if ((!wholeJob && !isDescendant(fullJob, fullTarget)) ||
                (wholeJob && !fullTarget.Equals(fullJob, pathComparison))) {
                throw new InvalidDataException("Каталог очистки не принадлежит заданию.");
            }
            // Проверяем родителей; конечный symlink удаляется как ссылка, без обхода цели.
            ensureNoSymbolicLinks(Path.GetDirectoryName(fullTarget)!);
            deleteTreeWithoutFollowingLinks(fullTarget);
            return Task.FromResult(true);
        }
        catch (Exception exception) when (isCleanupException(exception)) {
            logCleanupFailure(exception);
            return Task.FromResult(false);
        }
    }

    // проверяем имя папки, отсутствие ссылок и точное совпадение маркера с ID задания
    private bool isOwnedJob(string directory) {
        if (!isJobName(directory)) {
            return false;
        }
        try {
            ensureNoSymbolicLinks(directory);
            string markerPath = Path.Combine(directory, MarkerFileName);
            ensureNoSymbolicLinks(markerPath);
            FileInfo marker = new FileInfo(markerPath);
            return marker.Exists && marker.Length <= 128 &&
                File.ReadAllText(markerPath) == MarkerPrefix + Path.GetFileName(directory)[4..];
        }
        catch (Exception exception) when (isCleanupException(exception)) {
            return false;
        }
    }

    // допускаем только job-<32 hex> непосредственно в рабочем корне
    private bool isJobName(string directory) {
        return string.Equals(Path.GetDirectoryName(Path.GetFullPath(directory)), RootDirectory, pathComparison) &&
            Regex.IsMatch(Path.GetFileName(directory), "^job-[a-f0-9]{32}$");
    }

    // прерываем операцию, если папка не прошла проверку принадлежности приложению
    private void requireOwnedJob(string directory) {
        if (!isOwnedJob(Path.GetFullPath(directory))) {
            throw new InvalidDataException("Каталог не принадлежит заданию приложения.");
        }
    }

    // сравниваем полные пути с разделителем, чтобы общий текстовый префикс не считался вложенностью
    private bool isDescendant(string parent, string child) {
        return Path.GetFullPath(child).StartsWith(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent)) + Path.DirectorySeparatorChar, pathComparison);
    }

    // проверяем сам путь и всех родителей, при символической ссылке или reparse point выбрасываем ошибку
    private static void ensureNoSymbolicLinks(string path) {
        string? current = Path.GetFullPath(path);
        while (current is not null) {
            FileSystemInfo entry = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (entry.LinkTarget is not null || (entry.Exists && (entry.Attributes & FileAttributes.ReparsePoint) != 0)) {
                throw new InvalidDataException("Символические ссылки в рабочем пути запрещены.");
            }
            current = Path.GetDirectoryName(current);
        }
    }

    // удаляем содержимое рекурсивно; символическую ссылку удаляем как объект, не обходим ее цель
    private static void deleteTreeWithoutFollowingLinks(string path) {
        FileSystemInfo entry = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
        if (entry.LinkTarget is not null || (entry.Exists && (entry.Attributes & FileAttributes.ReparsePoint) != 0)) {
            if ((entry.Attributes & FileAttributes.Directory) != 0) {
                Directory.Delete(path);
            }
            else {
                File.Delete(path);
            }
            return;
        }
        if (!entry.Exists) {
            return;
        }
        if (entry is DirectoryInfo directory) {
            foreach (FileSystemInfo child in directory.EnumerateFileSystemInfos()) {
                deleteTreeWithoutFollowingLinks(child.FullName);
            }
            directory.Delete();
        }
        else {
            entry.Delete();
        }
    }

    // создаем ошибку invalid_output_path для недопустимого пути результата
    private static ExternalToolException invalidOutputPath() {
        return new ExternalToolException("Инструмент вернул недопустимый путь результата.", "invalid_output_path");
    }

    // определяем ошибки файловой операции, при которых очистка возвращает false
    private static bool isCleanupException(Exception exception) {
        return exception is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or
            NotSupportedException or ExternalToolException;
    }

    // записываем только тип ошибки очистки, файлы при неуспехе могут остаться
    private void logCleanupFailure(Exception exception) {
        logger.LogWarning("Working-file cleanup did not complete ({ExceptionType}); files may remain until restart.",
            exception.GetType().Name);
    }
}
