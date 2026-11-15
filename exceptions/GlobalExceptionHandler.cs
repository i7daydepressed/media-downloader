using MediaDownloader.Dto;
using Microsoft.AspNetCore.Diagnostics;

namespace MediaDownloader.Exceptions;

public sealed class GlobalExceptionHandler : IExceptionHandler {
    private readonly ILogger<GlobalExceptionHandler> logger;
    private readonly IHostApplicationLifetime lifetime;

    public GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger, IHostApplicationLifetime lifetime) {
        this.logger = logger;
        this.lifetime = lifetime;
    }

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken) {
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

        httpContext.Response.StatusCode = error.Status;
        try {
            await httpContext.Response.WriteAsJsonAsync(error, cancellationToken);
        }
        catch (OperationCanceledException) when (httpContext.RequestAborted.IsCancellationRequested) {
            httpContext.Abort();
        }
        return true;
    }

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
