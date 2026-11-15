using System.Threading.Channels;
using MediaDownloader.Configuration;
using Microsoft.Extensions.Options;

namespace MediaDownloader.Telegram;

// ограниченная очередь отделяет прием сообщений от последовательного скачивания файлов
public sealed class TelegramDownloadQueue {
    private readonly Channel<TelegramDownloadJob> channel; // очередь .NET с одним читателем-воркером

    // конструктор: задаем вместимость очереди из Telegram.QueueCapacity
    public TelegramDownloadQueue(IOptions<TelegramOptions> options) {
        channel = Channel.CreateBounded<TelegramDownloadJob>(new BoundedChannelOptions(options.Value.QueueCapacity) {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });
    }

    // добавляем задание без ожидания: при заполненной или закрытой очереди возвращаем false
    public bool tryEnqueue(TelegramDownloadJob job) => channel.Writer.TryWrite(job);
    // выдаем задания по мере появления, ожидание прерывается переданным токеном
    public IAsyncEnumerable<TelegramDownloadJob> readAllAsync(CancellationToken cancellationToken) =>
        channel.Reader.ReadAllAsync(cancellationToken);
    // закрываем добавление новых заданий в очередь
    public void complete() => channel.Writer.TryComplete();
}
