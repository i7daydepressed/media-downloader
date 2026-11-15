namespace MediaDownloader.Exceptions;

// ошибка запрета доступа, HTTP-обработчик возвращает 403
public sealed class AccessDeniedException : Exception {
    public string Code { get; } // код для ответа и выбора дальнейшего действия
    public string PublicMessage { get; } // сообщение, допустимое в ответе пользователю

    // конструктор: сохраняем сообщение и код ошибки
    public AccessDeniedException(string message = "Доступ запрещён.", string code = "access_denied")
        : base(message) {
        Code = code;
        PublicMessage = message;
    }
}
