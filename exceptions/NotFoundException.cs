namespace MediaDownloader.Exceptions;

// ошибка отсутствующего результата, HTTP-обработчик возвращает 404
public sealed class NotFoundException : Exception {
    public string Code { get; } // код для ответа и выбора дальнейшего действия
    public string PublicMessage { get; } // сообщение, допустимое в ответе пользователю

    // конструктор: сохраняем сообщение и код ошибки
    public NotFoundException(string message = "Результат не найден.", string code = "not_found")
        : base(message) {
        Code = code;
        PublicMessage = message;
    }
}
