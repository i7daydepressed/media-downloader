namespace MediaDownloader.Dto;

// единый JSON-ответ HTTP при ошибке
public sealed class ServiceErrorResponse {
    public string Code { get; init; } = string.Empty; // машинный код ошибки
    public string Message { get; init; } = string.Empty; // сообщение для пользователя
    public int Status { get; init; } // HTTP-статус ответа
    public string TraceId { get; init; } = string.Empty; // идентификатор запроса для сопоставления с логами
}
