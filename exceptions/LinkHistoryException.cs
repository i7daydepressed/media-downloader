namespace MediaDownloader.Exceptions;

// ошибка записи истории: перехватчик логирует ее и продолжает прием задания
public sealed class LinkHistoryException : Exception {
    public LinkHistoryException(Exception innerException)
        : base("Не удалось записать историю ссылок.", innerException) { }
}
