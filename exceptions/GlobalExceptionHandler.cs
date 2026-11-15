using MediaDownloader.Dto;
using Microsoft.AspNetCore.Diagnostics;

namespace MediaDownloader.Exceptions;

// преобразуем исключения HTTP-запроса в ServiceErrorResponse через интерфейс IExceptionHandler
public sealed class GlobalExceptionHandler : IExceptionHandler {
    private readonly ILogger<GlobalExceptionHandler> logger; // типы ошибок и идентификаторы запросов в логах
    private readonly IHostApplicationLifetime lifetime; // признак остановки сервера

    // конструктор: сохраняем журнал событий и сигнал остановки сервера
    public GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger, IHostApplicationLifetime lifetime) {
        this.logger = logger;
        this.lifetime = lifetime;
    }

    // выбираем HTTP-статус и безопасное сообщение ошибки, записываем JSON в ответ
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken) {
        // если ответ уже начат, обрываем соединение: заменить его тело JSON-ошибкой уже нельзя
        if (httpContext.Response.HasStarted) {
            logger.LogWarning("Response already started for request {TraceId}.", httpContext.TraceIdentifier);
            httpContext.Abort();
            return true;
        }
        if (exception is OperationCanceledException && httpContext.RequestAborted.IsCancellationRequested) {
            // Клиент уже отменил запрос: второе тело ответа не отправляем.
            httpContext.Response.StatusCode = StatusCodes.Status499ClientClosedRequest;
            return true;
        }

        // сопоставляем известные исключения статусам; для неожиданной ошибки используем общий ответ 500
        ServiceErrorResponse error = exception switch {
            OperationCanceledException when lifetime.ApplicationStopping.IsCancellationRequested => createResponse(
                httpContext, "server_stopping", "Приложение завершает работу.", 503),
            BadRequestException expected => createResponse(httpContext, expected.Code, expected.PublicMessage, 400),
            NotFoundException expected => createResponse(httpContext, expected.Code, expected.PublicMessage, 404),
            AccessDeniedException expected => createResponse(httpContext, expected.Code, expected.PublicMessage, 403),
            ExternalToolException expected => createResponse(httpContext, expected.Code, expected.PublicMessage, 502),
            BadHttpRequestException => createResponse(
                httpContext, "bad_request", "Некорректный HTTP-запрос.", 400),
            _ => createResponse(httpContext, "internal_error", "Внутренняя ошибка приложения.", 500)
        };
        if (error.Status == StatusCodes.Status500InternalServerError) {
            // Не записываем exception.Message/ToString: там могут быть token или вывод утилиты.
            logger.LogError("Unhandled {ExceptionType} for request {TraceId}.",
                exception.GetType().FullName, httpContext.TraceIdentifier);
        }

        // записываем выбранный статус и DTO ошибки; при отмене записи клиентом закрываем запрос
        httpContext.Response.StatusCode = error.Status;
        try {
            await httpContext.Response.WriteAsJsonAsync(error, cancellationToken);
        }
        catch (OperationCanceledException) when (httpContext.RequestAborted.IsCancellationRequested) {
            httpContext.Abort();
        }
        return true;
    }

    // собираем DTO ошибки из кода, сообщения, статуса и TraceIdentifier текущего HTTP-запроса
    public static ServiceErrorResponse createResponse(
        HttpContext httpContext, string code, string message, int status) {
        return new ServiceErrorResponse {
            Code = code,
            Message = message,
            Status = status,
            TraceId = httpContext.TraceIdentifier
        };
    }
}
