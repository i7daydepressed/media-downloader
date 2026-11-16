using System.Text.Json;
using System.Text.RegularExpressions;
using Castle.DynamicProxy;
using MediaDownloader.Proxies;
using MediaDownloader.Configuration;
using MediaDownloader.Exceptions;
using MediaDownloader.Services;
using MediaDownloader.Tools;
using MediaDownloader.Telegram;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace MediaDownloader;

// собираем HTTP-приложение, регистрируем службы и запускаем фоновые задачи Telegram
public class Program {
    public const string ApplicationVersion = "0.1.0"; // версия в ответе /health

    // создаем приложение с аргументами запуска и запускаем сервер
    public static void Main(string[] args) {
        WebApplication application = createApplication(args);
        application.Run();
    }

    // настраиваем HTTP, общие службы и Telegram, возвращаем готовое приложение
    public static WebApplication createApplication(
        string[] args, Action<WebApplicationBuilder>? configureBuilder = null) {
        // создаем builder: .NET читает конфигурацию, аргументы запуска и переменные окружения
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions {
            Args = args,
            ApplicationName = typeof(Program).Assembly.GetName().Name
        });
        // применяем дополнительную настройку builder, если она передана; адрес по умолчанию задаем только при отсутствии urls
        configureBuilder?.Invoke(builder);
        if (string.IsNullOrWhiteSpace(builder.Configuration["urls"])) {
            builder.WebHost.UseUrls("http://127.0.0.1:5080");
        }

        // подключаем контроллеры, имена полей JSON в camelCase и общий ответ 400 для неверного входного JSON
        builder.Services.AddControllers().AddJsonOptions(options => {
            options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        }).ConfigureApiBehaviorOptions(options => {
            options.InvalidModelStateResponseFactory = context => new BadRequestObjectResult(
                GlobalExceptionHandler.createResponse(context.HttpContext, "bad_request",
                    "Запрос содержит некорректный JSON или неверные данные.", 400));
        });
        builder.Services.ConfigureHttpJsonOptions(options => {
            options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        });
        // регистрируем HTTP-обработчик исключений и проверяемые настройки разделов Media, Tools и Telegram
        builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
        builder.Services.AddProblemDetails();
        addValidatedOptions(builder.Services);
        // AddSingleton создает по одному экземпляру каждой службы на приложение, оба входа используют общий MediaService
        builder.Services.AddSingleton<ExternalProcessRunner>();
        builder.Services.AddSingleton<WorkingDirectoryManager>();
        builder.Services.AddSingleton<FfmpegClient>();
        builder.Services.AddSingleton<FfprobeClient>();
        builder.Services.AddSingleton<YtDlpClient>();
        builder.Services.AddSingleton<GalleryDlClient>();
        builder.Services.AddSingleton<MediaPreparationService>();
        builder.Services.AddSingleton<MediaService>();
        builder.Services.AddSingleton<MediaArchiveWriter>();
        builder.Services.AddHostedService<WorkingDirectoryCleanupService>();
        // Отдельный HttpClient без логирующих factory handlers: endpoint Bot API содержит token.
        builder.Services.AddSingleton<TelegramApiClient>(provider => new TelegramApiClient(
            new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }),
            provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<TelegramOptions>>(),
            provider.GetRequiredService<ILogger<TelegramApiClient>>()));
        // очередь передает задания от обработчика сообщений воркеру, polling получает обновления в фоне
        builder.Services.AddSingleton<TelegramDownloadQueue>();
        builder.Services.AddSingleton<LinkHistoryWriter>();
        builder.Services.AddSingleton<SaveLinksInterceptor>();
        builder.Services.AddSingleton<ProxyGenerator>();

        builder.Services.AddSingleton<TelegramUpdateHandler>(
            TelegramUpdateHandler (IServiceProvider serviceProvider) => {
                
                ProxyGenerator proxyGenerator = serviceProvider.GetRequiredService<ProxyGenerator>();
                
                SaveLinksProxyGenerationHook methodSelection = new SaveLinksProxyGenerationHook();

                ProxyGenerationOptions proxyOptions = new ProxyGenerationOptions(methodSelection);

                // получаем из контекста пять объектов для конструктора базового TelegramUpdateHandler
                object[] handlerConstructorArguments = new object[] {
                    serviceProvider.GetRequiredService<IOptions<TelegramOptions>>(),
                    serviceProvider.GetRequiredService<TelegramApiClient>(),
                    serviceProvider.GetRequiredService<TelegramDownloadQueue>(),
                    serviceProvider.GetRequiredService<MediaService>(),
                    serviceProvider.GetRequiredService<ILogger<TelegramUpdateHandler>>()
                };

                SaveLinksInterceptor linksInterceptor = serviceProvider.GetRequiredService<SaveLinksInterceptor>();
                // создаем наследника: настройки выбора методов, аргументы базового конструктора и перехватчик
                TelegramUpdateHandler handlerProxy = proxyGenerator.CreateClassProxy<TelegramUpdateHandler>(
                    proxyOptions, handlerConstructorArguments, linksInterceptor);
                return handlerProxy;
            });
        builder.Services.AddHostedService<TelegramDownloadWorker>();
        builder.Services.AddHostedService<TelegramPollingService>();

        // собираем приложение, включаем обработку исключений, маршруты контроллеров и ответ /health
        WebApplication application = builder.Build();
        application.UseExceptionHandler();
        application.MapControllers();
        application.MapGet("/health", () => Results.Ok(new {
            status = "ok",
            version = ApplicationVersion
        }));
        return application;
    }

    // связываем разделы конфигурации с классами настроек и отклоняем неверные значения при запуске
    private static void addValidatedOptions(IServiceCollection services) {
        // проверяем лимиты скачивания и рабочий путь из раздела Media
        services.AddOptions<MediaOptions>().BindConfiguration(MediaOptions.SectionName)
            .Validate(options => options.MaxUrlsPerBatch is >= 1 and <= 100,
                "Media:MaxUrlsPerBatch must be between 1 and 100.")
            .Validate(options => options.MaxFilesPerUrl is >= 1 and <= 1000,
                "Media:MaxFilesPerUrl must be between 1 and 1000.")
            .Validate(options => options.MaxConcurrentOperations is >= 1 and <= 16,
                "Media:MaxConcurrentOperations must be between 1 and 16.")
            .Validate(options => isValidPath(options.WorkingRoot),
                "Media:WorkingRoot must be a valid nonempty path.")
            .Validate(options => options.MaxFileSizeBytes is >= 1 and <= 2_000_000_000,
                "Media:MaxFileSizeBytes must be between 1 and 2000000000.")
            .ValidateOnStart();

        // проверяем пути утилит, имя JS-среды, время процесса и размер сохраняемого вывода
        services.AddOptions<ToolOptions>().BindConfiguration(ToolOptions.SectionName)
            .Validate(options => isValidPath(options.YtDlpPath) && isValidPath(options.GalleryDlPath) && isValidPath(options.FfmpegPath) &&
                isValidPath(options.FfprobePath) && isValidPath(options.JavaScriptRuntimePath),
                "Tools executable paths must be valid nonempty paths.")
            .Validate(options => !string.IsNullOrWhiteSpace(options.JavaScriptRuntime) &&
                Regex.IsMatch(options.JavaScriptRuntime, "^[a-zA-Z0-9_-]+$"),
                "Tools:JavaScriptRuntime must be a runtime name.")
            .Validate(options => options.ProcessTimeoutSeconds is >= 1 and <= 86_400,
                "Tools:ProcessTimeoutSeconds must be between 1 and 86400.")
            .Validate(options => options.MaxCapturedOutputCharacters is >= 1024 and <= 1_048_576,
                "Tools:MaxCapturedOutputCharacters must be between 1024 and 1048576.")
            .ValidateOnStart();

        // проверяем реквизиты и лимиты Telegram; формат токена обязателен при включенном адаптере
        services.AddOptions<TelegramOptions>().BindConfiguration(TelegramOptions.SectionName)
            .Validate(options => isValidPath(options.LinksHistoryPath),
                "Telegram:LinksHistoryPath must be a valid nonempty path.")
            .Validate(options => !options.Enabled || (!string.IsNullOrWhiteSpace(options.BotToken) &&
                Regex.IsMatch(options.BotToken, "^[0-9]+:[A-Za-z0-9_-]+$")),
                "Telegram:BotToken is required in token format when Telegram is enabled.")
            .Validate(options => options.AllowedUserIds is not null && options.AllowedUserIds.All(id => id > 0),
                "Telegram:AllowedUserIds must contain positive numeric user IDs.")
            .Validate(options => isValidBotApiUrl(options.BaseUrl, options.UseLocalBotApi),
                "Telegram:BaseUrl must be an absolute Bot API URL without credentials, query or fragment; cloud mode requires HTTPS.")
            .Validate(options => options.MaxUploadSizeBytes >= 1 &&
                options.MaxUploadSizeBytes <= (options.UseLocalBotApi ? 2_000_000_000 : 50_000_000),
                "Telegram:MaxUploadSizeBytes exceeds the selected Bot API mode limit.")
            .Validate(options => options.QueueCapacity is >= 1 and <= 1000,
                "Telegram:QueueCapacity must be between 1 and 1000.")
            .Validate(options => options.PollingTimeoutSeconds is >= 1 and <= 50,
                "Telegram:PollingTimeoutSeconds must be between 1 and 50.")
            .Validate(options => options.RetryDelaySeconds is >= 1 and <= 60,
                "Telegram:RetryDelaySeconds must be between 1 and 60.")
            .Validate(options => options.UploadTimeoutSeconds is >= 1 and <= 86_400,
                "Telegram:UploadTimeoutSeconds must be between 1 and 86400.")
            .ValidateOnStart();
    }

    // проверяем допустимость записи пути без создания файла и проверки его существования
    private static bool isValidPath(string? value) {
        if (string.IsNullOrWhiteSpace(value) || value.IndexOfAny(new[] { '\0', '\r', '\n' }) >= 0) {
            return false;
        }
        try {
            Path.GetFullPath(value);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) {
            return false;
        }
    }

    // проверяем адрес Bot API: HTTPS, либо HTTP в локальном режиме, без реквизитов и дополнительных частей URL
    private static bool isValidBotApiUrl(string? value, bool localMode) {
        return Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) &&
            (uri.Scheme == Uri.UriSchemeHttps || (localMode && uri.Scheme == Uri.UriSchemeHttp)) &&
            string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment);
    }
}
