using System.Threading.Channels;
using MediaDownloader.Configuration;
using Microsoft.Extensions.Options;

namespace MediaDownloader.Telegram;

public sealed class TelegramDownloadQueue {
    private readonly Channel<TelegramDownloadJob> channel;

    public TelegramDownloadQueue(IOptions<TelegramOptions> options) {
        channel = Channel.CreateBounded<TelegramDownloadJob>(new BoundedChannelOptions(options.Value.QueueCapacity) {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });
    }

    public bool tryEnqueue(TelegramDownloadJob job) => channel.Writer.TryWrite(job);
    public IAsyncEnumerable<TelegramDownloadJob> readAllAsync(CancellationToken cancellationToken) =>
        channel.Reader.ReadAllAsync(cancellationToken);
    public void complete() => channel.Writer.TryComplete();
}
