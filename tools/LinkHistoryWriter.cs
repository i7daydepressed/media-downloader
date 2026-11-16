using System.Globalization;
using System.Security;
using System.Text;
using MediaDownloader.Configuration;
using MediaDownloader.Exceptions;
using Microsoft.Extensions.Options;

namespace MediaDownloader.Tools;

// служба дозаписи URL в TXT: одна строка содержит UTC-дату и ссылку, прежние записи сохраняются
public sealed class LinkHistoryWriter : IDisposable {
    private readonly string filePath; // полный путь истории вне временных папок загрузок
    private readonly SemaphoreSlim writeGate = new SemaphoreSlim(1, 1); // одна запись в файл за раз

    // конструктор: путь из Telegram:LinksHistoryPath разрешаем относительно каталога приложения
    public LinkHistoryWriter(IOptions<TelegramOptions> options, IHostEnvironment environment) {
        filePath = Path.GetFullPath(options.Value.LinksHistoryPath, environment.ContentRootPath);
    }

    // дописываем дату и ссылки в исходном порядке, включая повторы; отмена прерывает запись
    public async Task appendAsync(IReadOnlyList<string> urls, CancellationToken cancellationToken) {
        cancellationToken.ThrowIfCancellationRequested();
        string timestamp = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        StringBuilder lines = new StringBuilder();
        foreach (string url in urls) {
            // запрещаем переносы и управляющие разделители, чтобы одна ссылка оставалась одной строкой
            if (string.IsNullOrWhiteSpace(url) || url.Any(char.IsControl)) {
                throw new ArgumentException("История принимает непустые ссылки без управляющих символов.", nameof(urls));
            }
            lines.Append(timestamp).Append('\t').Append(url).Append('\n');
        }
        if (lines.Length == 0) {
            return;
        }
        await writeGate.WaitAsync(cancellationToken);
        try {
            // создаем каталог при первой записи и дописываем UTF-8 без перезаписи истории
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            await File.AppendAllTextAsync(filePath, lines.ToString(), new UTF8Encoding(false), cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException) {
            throw new LinkHistoryException(exception);
        }
        finally {
            writeGate.Release();
        }
    }

    // DI вызывает Dispose при завершении приложения: освобождаем ограничитель записи
    public void Dispose() => writeGate.Dispose();
}
