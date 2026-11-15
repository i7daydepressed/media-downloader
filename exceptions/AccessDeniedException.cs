namespace MediaDownloader.Exceptions;

public sealed class AccessDeniedException : Exception {
    public string Code { get; }
    public string PublicMessage { get; }

    public AccessDeniedException(string message = "Доступ запрещён.", string code = "access_denied")
        : base(message) {
        Code = code;
        PublicMessage = message;
    }
}
