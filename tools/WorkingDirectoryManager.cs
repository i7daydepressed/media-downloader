using System.Globalization;
using System.Text.RegularExpressions;
using MediaDownloader.Configuration;
using MediaDownloader.Exceptions;
using Microsoft.Extensions.Options;

namespace MediaDownloader.Tools;

public sealed class WorkingDirectoryManager {
    private const string MarkerFileName = ".media-downloader-job";
    private const string MarkerPrefix = "media-downloader:0.1.0:";
    private readonly ILogger<WorkingDirectoryManager> logger;
    private readonly StringComparison pathComparison;

    public string RootDirectory { get; }

    public WorkingDirectoryManager(IOptions<MediaOptions> options, IHostEnvironment environment,
        ILogger<WorkingDirectoryManager> logger) {
        RootDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.Value.WorkingRoot, environment.ContentRootPath));
        this.logger = logger;
        pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    }

    public Task initializeAsync(CancellationToken cancellationToken) {
        cancellationToken.ThrowIfCancellationRequested();
        ensureNoSymbolicLinks(RootDirectory);
        Directory.CreateDirectory(RootDirectory);
        ensureNoSymbolicLinks(RootDirectory);
        return Task.CompletedTask;
    }

    public string createJobDirectory() {
        ensureNoSymbolicLinks(RootDirectory);
        string id = Guid.NewGuid().ToString("N");
        string directory = Path.Combine(RootDirectory, "job-" + id);
        Directory.CreateDirectory(directory);
        ensureNoSymbolicLinks(directory);
        using FileStream marker = new FileStream(Path.Combine(directory, MarkerFileName),
            FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using StreamWriter writer = new StreamWriter(marker);
        writer.Write(MarkerPrefix + id);
        return directory;
    }

    public string validateJobDirectory(string jobDirectory) {
        string directory = Path.GetFullPath(jobDirectory);
        requireOwnedJob(directory);
        return directory;
    }

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

    public string validateFilePath(string jobDirectory, string filePath, string? attemptDirectory = null) {
        requireOwnedJob(jobDirectory);
        string fullPath;
        try {
            fullPath = Path.GetFullPath(filePath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException) {
            throw invalidOutputPath();
        }
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

    public string createPreparationDirectory(string jobDirectory, string sourcePath) {
        string parent = Path.GetDirectoryName(validateFilePath(jobDirectory, sourcePath))!;
        string directory = Path.Combine(parent, "prepared-" + Guid.NewGuid().ToString("N"));
        ensureNoSymbolicLinks(directory);
        Directory.CreateDirectory(directory);
        ensureNoSymbolicLinks(directory);
        return directory;
    }

    public Task<bool> cleanupAttemptAsync(string jobDirectory, string attemptDirectory) {
        return cleanupAsync(jobDirectory, attemptDirectory, false);
    }

    public Task<bool> cleanupJobAsync(string? jobDirectory) {
        if (jobDirectory is null) {
            return Task.FromResult(true);
        }
        return cleanupAsync(jobDirectory, jobDirectory, true);
    }

    public Task<bool> deleteDeliveredFileAsync(string jobDirectory, string filePath) {
        return deleteOwnedFileAsync(jobDirectory, filePath);
    }

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
            File.Delete(fullPath);
            return Task.FromResult(true);
        }
        catch (Exception exception) when (isCleanupException(exception)) {
            logCleanupFailure(exception);
            return Task.FromResult(false);
        }
    }

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

    private Task<bool> cleanupAsync(string jobDirectory, string target, bool wholeJob) {
        try {
            string fullJob = Path.GetFullPath(jobDirectory);
            if (wholeJob && isJobName(fullJob) && !Directory.Exists(fullJob) && !File.Exists(fullJob)) {
                ensureNoSymbolicLinks(fullJob);
                return Task.FromResult(true);
            }
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

    private bool isJobName(string directory) {
        return string.Equals(Path.GetDirectoryName(Path.GetFullPath(directory)), RootDirectory, pathComparison) &&
            Regex.IsMatch(Path.GetFileName(directory), "^job-[a-f0-9]{32}$");
    }

    private void requireOwnedJob(string directory) {
        if (!isOwnedJob(Path.GetFullPath(directory))) {
            throw new InvalidDataException("Каталог не принадлежит заданию приложения.");
        }
    }

    private bool isDescendant(string parent, string child) {
        return Path.GetFullPath(child).StartsWith(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent)) + Path.DirectorySeparatorChar, pathComparison);
    }

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

    private static ExternalToolException invalidOutputPath() {
        return new ExternalToolException("Инструмент вернул недопустимый путь результата.", "invalid_output_path");
    }

    private static bool isCleanupException(Exception exception) {
        return exception is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or
            NotSupportedException or ExternalToolException;
    }

    private void logCleanupFailure(Exception exception) {
        logger.LogWarning("Working-file cleanup did not complete ({ExceptionType}); files may remain until restart.",
            exception.GetType().Name);
    }
}
