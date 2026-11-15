namespace MediaDownloader.Exceptions;

public sealed class BadRequestException : Exception {
    public string Code { get; }
    public string PublicMessage { get; }

    public BadRequestException(string message = "Некорректный запрос.", string code = "bad_request")
        : base(message) {
        Code = code;
        PublicMessage = message;
    }
}
