namespace MediaDownloader.Exceptions;

public sealed class ExternalToolException : Exception {
    public string Code { get; }
    public string PublicMessage { get; }

    public ExternalToolException(
        string message = "Не удалось обработать медиа.",
        string code = "external_tool_error",
        Exception? innerException = null) : base(message, innerException) {
        Code = code;
        PublicMessage = message;
    }
}
