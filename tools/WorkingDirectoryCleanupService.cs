namespace MediaDownloader.Tools;

public sealed class WorkingDirectoryCleanupService : IHostedService {
    private readonly WorkingDirectoryManager directories;

    public WorkingDirectoryCleanupService(WorkingDirectoryManager directories) {
        this.directories = directories;
    }

    public async Task StartAsync(CancellationToken cancellationToken) {
        await directories.initializeAsync(cancellationToken);
        await directories.cleanupStaleJobsAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) {
        return Task.CompletedTask;
    }
}
