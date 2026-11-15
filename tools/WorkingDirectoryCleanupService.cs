namespace MediaDownloader.Tools;

// стартовая служба: готовим рабочую папку и убираем оставшиеся задания
public sealed class WorkingDirectoryCleanupService : IHostedService {
    private readonly WorkingDirectoryManager directories; // создание корня и очистка принадлежащих приложению папок

    // конструктор: сохраняем службу каталогов
    public WorkingDirectoryCleanupService(WorkingDirectoryManager directories) {
        this.directories = directories;
    }

    // создаем рабочий корень и очищаем оставшиеся собственные задания
    public async Task StartAsync(CancellationToken cancellationToken) {
        await directories.initializeAsync(cancellationToken);
        await directories.cleanupStaleJobsAsync(cancellationToken);
    }

    // при остановке отдельную очистку не запускаем
    public Task StopAsync(CancellationToken cancellationToken) {
        return Task.CompletedTask;
    }
}
