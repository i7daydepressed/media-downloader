namespace MediaDownloader.Exceptions;

// ошибка входных данных, HTTP-обработчик возвращает 400
public sealed class BadRequestException : Exception {
    public string Code { get; } // код для ответа и выбора дальнейшего действия
    public string PublicMessage { get; } // сообщение, допустимое в ответе пользователю

    // конструктор: сохраняем сообщение и код ошибки
    public BadRequestException(string message = "Некорректный запрос.", string code = "bad_request")
        : base(message) {
        Code = code;
        PublicMessage = message;
    }
}
