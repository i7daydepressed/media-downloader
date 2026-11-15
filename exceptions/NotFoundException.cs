namespace MediaDownloader.Exceptions;

public sealed class NotFoundException : Exception {
    public string Code { get; }
    public string PublicMessage { get; }

    public NotFoundException(string message = "Результат не найден.", string code = "not_found")
        : base(message) {
        Code = code;
        PublicMessage = message;
    }
}
