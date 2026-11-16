namespace MediaDownloader.Annotations;

// отмечаем метод передачи проверенных ссылок: его вызов должен дополнительно сохранять историю
[AttributeUsage(AttributeTargets.Method, Inherited = true)]
public sealed class SaveLinksAttribute : Attribute { }
