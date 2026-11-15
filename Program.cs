using System.Text.Json;
using System.Text.RegularExpressions;
using MediaDownloader.Configuration;
using MediaDownloader.Exceptions;
using MediaDownloader.Services;
using MediaDownloader.Tools;
using MediaDownloader.Telegram;
using Microsoft.AspNetCore.Mvc;

namespace MediaDownloader;

public class Program {
    public const string ApplicationVersion = "0.1.0";

    public static void Main(string[] args) {
        WebApplication application = createApplication(args);
        application.Run();
    }

    public static WebApplication createApplication(
        string[] args, Action<WebApplicationBuilder>? configureBuilder = null) {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions {
            Args = args,
            ApplicationName = typeof(Program).Assembly.GetName().Name
        });
        configureBuilder?.Invoke(builder);
        if (string.IsNullOrWhiteSpace(builder.Configuration["urls"])) {
            builder.WebHost.UseUrls("http://127.0.0.1:5080");
        }

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
        builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
        builder.Services.AddProblemDetails();
        addValidatedOptions(builder.Services);
        builder.Services.AddSingleton<ExternalProcessRunner>();
        builder.Services.AddSingleton<WorkingDirectoryManager>();
        builder.Services.AddSingleton<FfmpegClient>();
        builder.Services.AddSingleton<FfprobeClient>();
        builder.Services.AddSingleton<YtDlpClient>();
        builder.Services.AddSingleton<GalleryDlClient>();
        builder.Services.AddSingleton<MediaService>();
        builder.Services.AddSingleton<MediaArchiveWriter>();
        builder.Services.AddHostedService<WorkingDirectoryCleanupService>();
        // Отдельный HttpClient без логирующих factory handlers: endpoint Bot API содержит token.
        builder.Services.AddSingleton<TelegramApiClient>(provider => new TelegramApiClient(
            new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }),
            provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<TelegramOptions>>(),
            provider.GetRequiredService<ILogger<TelegramApiClient>>()));
        builder.Services.AddSingleton<TelegramDownloadQueue>();
        builder.Services.AddSingleton<TelegramUpdateHandler>();
        builder.Services.AddHostedService<TelegramDownloadWorker>();
        builder.Services.AddHostedService<TelegramPollingService>();

        WebApplication application = builder.Build();
        application.UseExceptionHandler();
        application.MapControllers();
        application.MapGet("/health", () => Results.Ok(new {
            status = "ok",
            version = ApplicationVersion
        }));
        return application;
    }

    private static void addValidatedOptions(IServiceCollection services) {
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

        services.AddOptions<TelegramOptions>().BindConfiguration(TelegramOptions.SectionName)
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

    private static bool isValidBotApiUrl(string? value, bool localMode) {
        return Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) &&
            (uri.Scheme == Uri.UriSchemeHttps || (localMode && uri.Scheme == Uri.UriSchemeHttp)) &&
            string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment);
    }
}
