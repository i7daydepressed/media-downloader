using Castle.DynamicProxy;
using MediaDownloader.Dto;
using MediaDownloader.Exceptions;
using MediaDownloader.Tools;

namespace MediaDownloader.Proxies;

// выполняем запись истории перед исходным методом, выбранным по атрибуту SaveLinks
public sealed class SaveLinksInterceptor : IInterceptor {
    private readonly LinkHistoryWriter history; // служба дозаписи даты и URL в TXT
    private readonly ILogger<SaveLinksInterceptor> logger; // ошибки истории без URL и служебных путей

    public SaveLinksInterceptor(LinkHistoryWriter history, ILogger<SaveLinksInterceptor> logger) {
        this.history = history;
        this.logger = logger;
    }

    // Castle передает метод и его аргументы; возвращаем вызывающему коду общую асинхронную операцию
    public void Intercept(IInvocation invocation) {
        MediaRequest request = invocation.Arguments.OfType<MediaRequest>().Single();
        CancellationToken cancellationToken = invocation.Arguments.OfType<CancellationToken>().Single();
        string[] urls = request.Urls!.ToArray(); // снимок уже проверенных ссылок до первого await
        // сохраняем продолжение до await, чтобы затем вызвать базовый метод один раз без повторного перехвата
        IInvocationProceedInfo proceed = invocation.CaptureProceedInfo();
        invocation.ReturnValue = saveAndProceedAsync(invocation, proceed, urls, cancellationToken);
    }

    // сначала записываем историю, затем продолжаем исходный метод и возвращаем его bool
    private async Task<bool> saveAndProceedAsync(IInvocation invocation, IInvocationProceedInfo proceed,
        string[] urls, CancellationToken cancellationToken) {
        cancellationToken.ThrowIfCancellationRequested();
        try {
            // здесь напрямую вызываем службу записи TXT; отдельный сервис хранения пока не выделяем
            await history.appendAsync(urls, cancellationToken);
        }
        catch (LinkHistoryException exception) {
            // ошибка истории не отменяет скачивание; исходные URL и сообщение системной ошибки не логируем
            logger.LogWarning("Link history write failed after {ExceptionType}; accepting the download continues.",
                exception.InnerException?.GetType().Name ?? exception.GetType().Name);
        }
        cancellationToken.ThrowIfCancellationRequested();
        // Castle вызывает реализацию базового класса; ее исключение или отмена проходят к вызывающему коду
        proceed.Invoke();
        return await (Task<bool>)invocation.ReturnValue!;
    }
}
