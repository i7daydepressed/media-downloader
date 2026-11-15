namespace MediaDownloader.Exceptions;

// ошибка внешней утилиты или ее результата, HTTP-обработчик возвращает 502
public sealed class ExternalToolException : Exception {
    public string Code { get; } // код для ответа и выбора дальнейшего действия
    public string PublicMessage { get; } // сообщение, допустимое в ответе пользователю

    // конструктор: сохраняем сообщение и код ошибки
    public ExternalToolException(
        string message = "Не удалось обработать медиа.",
        string code = "external_tool_error",
        Exception? innerException = null) : base(message, innerException) {
        Code = code;
        PublicMessage = message;
    }
}
