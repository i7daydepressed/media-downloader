using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using MediaDownloader.Configuration;
using MediaDownloader.Dto;
using MediaDownloader.Exceptions;
using MediaDownloader.Tools;
using Microsoft.Extensions.Options;

namespace MediaDownloader.Services;

// сервис оркестрирует службы и обеспеч выполнение бизнес-операций
// - HTTP и Telegram используют этот общий сценарий работы со службами и временными файлами
// IDisposable: при остановке сервера .NET вызывает Dispose, где завершаем использование gate
public sealed class MediaService : IDisposable {
    private readonly MediaOptions options; // настройки скачивания
    private readonly YtDlpClient ytDlp; // служба скачивания через yt-dlp
    private readonly GalleryDlClient galleryDl; // запасная служба скачивания через gallery-dl
    private readonly FfprobeClient ffprobe; // служба чтения характеристик медиафайла
    private readonly MediaPreparationService preparation; // пробует перепаковать MKV в MP4 без перекодирования
    private readonly WorkingDirectoryManager directories; // создание, проверка и удаление временных файлов и папок
    private readonly IHostApplicationLifetime lifetime; // сигнал остановки сервера через ApplicationStopping
    private readonly ILogger<MediaService> logger; // запись событий скачивания в логи
    private readonly SemaphoreSlim gate; // ограничивает число одновременно скачиваемых запросов

    // конструктор: сохраняем настройки и службы, задаем лимит параллельных скачиваний
    public MediaService(IOptions<MediaOptions> options, YtDlpClient ytDlp, GalleryDlClient galleryDl,
        FfprobeClient ffprobe, MediaPreparationService preparation, WorkingDirectoryManager directories, IHostApplicationLifetime lifetime,
        ILogger<MediaService> logger) {
        // Program.addValidatedOptions привязывает раздел Media из конфигурации к MediaOptions
        this.options = options.Value; // настройки Media из appsettings.json и переопределений конфигурации
        this.ytDlp = ytDlp;
        this.galleryDl = galleryDl;
        this.ffprobe = ffprobe;
        this.preparation = preparation;
        this.directories = directories;
        this.lifetime = lifetime;
        this.logger = logger;
        // задаем лимит одновременных запросов на скачивание из MaxConcurrentOperations
        gate = new SemaphoreSlim(this.options.MaxConcurrentOperations, this.options.MaxConcurrentOperations);
    }

    // принимаем DTO входных данных (MediaRequest), в нем список ссылок
    public Task<MediaBatchResult> downloadAsync(MediaRequest request, CancellationToken cancellationToken) {
        // передаем DTO со списком ссылок (MediaRequest request) в основной метод скачивания и проверки файлов (downloadAsync с тремя параметрами)
        // - вторым аргументом передаем false: отключаем предпочтение MP4 с H.264/AAC и попытку перепаковки скачанного MKV в MP4
        // - третьим передаем объект проверки отмены обработки запроса (CancellationToken cancellationToken)
        return downloadAsync(request, false, cancellationToken);
    }

    // связываем проверку входа, вызовы служб, отмену и время жизни файлов в одном сценарии
    public async Task<MediaBatchResult> downloadAsync(MediaRequest request, bool preferCompatibleVideo,
        CancellationToken cancellationToken) {
        // проверяем ссылки через validateRequest; при неверных данных он выбрасывает BadRequestException
        List<string> urls = validateRequest(request);
        // через CreateLinkedTokenSource объединяем отмену запроса и остановку сервера
        // общий токен сохраняем в operationToken
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, lifetime.ApplicationStopping);
        CancellationToken operationToken = linked.Token;
        // ждем, пока можно начать еще одно скачивание в пределах заданного лимита
        await gate.WaitAsync(operationToken);
        string? jobDirectory = null; // полный путь временной папки запроса, пока она не создана
        try {
            // при запрошенной отмене ThrowIfCancellationRequested выбрасывает OperationCanceledException и прерывает запрос
            operationToken.ThrowIfCancellationRequested();
            // через createJobDirectory создаем папку job-<id> в рабочем каталоге WorkingRoot и получаем ее полный путь
            jobDirectory = directories.createJobDirectory();
            List<MediaResult> results = new List<MediaResult>(); // результаты ссылок: описания файлов или ошибка
            // по очереди скачиваем ссылки через downloadItemAsync и добавляем MediaResult в results через Add
            // перед каждой ссылкой проверяем отмену; результат с Success=false сохраняем и продолжаем
            for (int index = 0; index < urls.Count; index++) {
                operationToken.ThrowIfCancellationRequested();
                results.Add(await downloadItemAsync(jobDirectory, index, urls[index], preferCompatibleVideo, operationToken));
            }
            // учитываем отмену, которая могла поступить во время последнего скачивания
            operationToken.ThrowIfCancellationRequested();
            // возвращаем результаты ссылок и путь временной папки в DTO MediaBatchResult
            // HTTP-контроллер или обработчик Telegram читает файлы по этим путям и отвечает за дальнейшую очистку
            return new MediaBatchResult { Results = results, WorkingDirectory = jobDirectory };
        }
        catch {
            // при исключении из try сначала пробуем удалить папку запроса через cleanupJobAsync
            // bool очистки здесь не проверяем; throw передает пойманное исключение выше, если очистка сама не выбросила новое
            await directories.cleanupJobAsync(jobDirectory);
            throw;
        }
        finally {
            // при завершении или ошибке скачивания Release позволяет следующему запросу пройти WaitAsync
            gate.Release();
        }
    }

    // очищаем временные файлы уже возвращенного результата (batch), который передает HTTP-контроллер или обработчик Telegram
    public Task<bool> cleanupAsync(MediaBatchResult batch) {
        // передаем службе очистки путь из batch.WorkingDirectory
        // true — очищено или удалять нечего; false — перехваченная ошибка проверки или удаления
        return directories.cleanupJobAsync(batch.WorkingDirectory);
    }

    // удаляем файл из результата batch после отправки; подтверждение отправки проверяет обработчик Telegram
    public Task<bool> deleteDeliveredFileAsync(MediaBatchResult batch, MediaFileResult file) {
        // проверяем пути и ищем тот же экземпляр MediaFileResult file среди Files всех результатов batch.Results
        // SelectMany объединяет эти списки, Contains проверяет принадлежность экземпляра; при отказе возвращаем false
        if (batch.WorkingDirectory is null || file.FilePath is null ||
            !batch.Results.SelectMany(result => result.Files).Contains(file)) {
            return Task.FromResult(false);
        }
        // передаем службе полный FilePath и папку запроса для проверки принадлежности и удаления файла
        return directories.deleteDeliveredFileAsync(batch.WorkingDirectory, file.FilePath);
    }

    // скачиваем один URL через yt-dlp; при ExternalToolException первой попытки после очистки пробуем gallery-dl
    private async Task<MediaResult> downloadItemAsync(string jobDirectory, int index, string url, bool preferCompatibleVideo,
        CancellationToken cancellationToken) {
        string firstAttempt = directories.createAttemptDirectory(jobDirectory, index, "yt-dlp"); // полный путь папки попытки yt-dlp
        try {
            // скачиваем URL в firstAttempt через службу ytDlp и получаем список полных путей готовых файлов
            List<string> paths = await ytDlp.downloadAsync(url, firstAttempt, preferCompatibleVideo, cancellationToken);
            // yt-dlp должен вернуть один файл; иначе переходим в обработчик ExternalToolException
            if (paths.Count != 1) {
                throw new ExternalToolException("Источник не вернул один итоговый видеофайл.", "output_missing");
            }
            // проверяем файлы через validateFilesAsync и получаем их описания MediaFileResult
            List<MediaFileResult> files = await validateFilesAsync(jobDirectory, firstAttempt, index, paths, cancellationToken);
            if (preferCompatibleVideo) {
                // при preferCompatibleVideo пробуем подготовить видео через preparation и берем обновленный список файлов
                files = await preparation.prepareAsync(jobDirectory, files, cancellationToken);
            }
            return new MediaResult { Index = index, Url = url, Success = true, Files = files };
        }
        catch (ExternalToolException exception) {
            // при ExternalToolException записываем причину и просим directories удалить папку первой попытки
            // если очистка не удалась, IOException прерывает весь запрос
            logger.LogInformation("Media item {Index} uses fallback after {Code}.", index, exception.Code);
            if (!await directories.cleanupAttemptAsync(jobDirectory, firstAttempt)) {
                throw new IOException("Не удалось очистить неуспешную попытку загрузки.");
            }
        }

        // проверяем отмену перед запасным скачиванием
        cancellationToken.ThrowIfCancellationRequested();
        string secondAttempt = directories.createAttemptDirectory(jobDirectory, index, "gallery-dl"); // папка запасной попытки
        try {
            // gallery-dl может вернуть несколько файлов; проверяем их и при запросе обновляем через preparation
            List<string> paths = await galleryDl.downloadAsync(url, secondAttempt, preferCompatibleVideo, cancellationToken);
            List<MediaFileResult> files = await validateFilesAsync(jobDirectory, secondAttempt, index, paths, cancellationToken);
            if (preferCompatibleVideo) {
                files = await preparation.prepareAsync(jobDirectory, files, cancellationToken);
            }
            return new MediaResult { Index = index, Url = url, Success = true, Files = files };
        }
        catch (ExternalToolException exception) {
            // при ExternalToolException второй попытки пробуем очистить ее папку; отказ прерывает весь запрос через IOException
            if (!await directories.cleanupAttemptAsync(jobDirectory, secondAttempt)) {
                throw new IOException("Не удалось очистить неуспешную попытку загрузки.");
            }
            // сохраняем ошибку этой ссылки в MediaResult, чтобы основной цикл продолжил остальные ссылки
            return new MediaResult {
                Index = index, Url = url, Success = false, ErrorCode = exception.Code, ErrorMessage = exception.PublicMessage
            };
        }
    }

    // проверяем файлы одной попытки и собираем DTO с их путями и характеристиками; нарушения вызывают ExternalToolException
    private async Task<List<MediaFileResult>> validateFilesAsync(string jobDirectory, string attemptDirectory,
        int urlIndex, List<string> paths, CancellationToken cancellationToken) {
        // проверяем, что результат не пуст и число файлов не превышает MaxFilesPerUrl
        if (paths.Count == 0) {
            throw new ExternalToolException("Источник не вернул готовых медиафайлов.", "output_missing");
        }
        if (paths.Count > options.MaxFilesPerUrl) {
            throw new ExternalToolException("Превышено допустимое число файлов для одной ссылки.", "too_many_files");
        }
        HashSet<string> seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal); // проверенные пути, на Windows без учета регистра
        List<MediaFileResult> files = new List<MediaFileResult>();
        for (int fileIndex = 0; fileIndex < paths.Count; fileIndex++) {
            cancellationToken.ThrowIfCancellationRequested();
            // проверяем принадлежность файла папкам запроса и попытки, отсутствие символических ссылок и наличие файла
            string path = directories.validateFilePath(jobDirectory, paths[fileIndex], attemptDirectory);
            // проверяем расширение и отсекаем служебные файлы, в том числе .part и .json
            string extension = Path.GetExtension(path).ToLowerInvariant();
            if (!Regex.IsMatch(extension, "^\\.[a-z0-9]{1,12}$") ||
                extension is ".part" or ".ytdl" or ".json" or ".txt" or ".tmp" or ".log" or ".srt" or ".vtt") {
                throw new ExternalToolException("Инструмент вернул служебный файл вместо медиа.", "invalid_media_file");
            }
            // seen.Add возвращает false, если инструмент повторно вернул тот же путь
            if (!seen.Add(path)) {
                throw new ExternalToolException("Список результата содержит повторяющийся файл.", "invalid_tool_output");
            }
            long size = new FileInfo(path).Length; // размер файла на диске в байтах
            // отклоняем пустой файл или превышение MaxFileSizeBytes
            if (size == 0 || size > options.MaxFileSizeBytes) {
                throw new ExternalToolException("Итоговый файл пуст или превышает допустимый размер.", "file_size_limit");
            }
            MediaProbeResult probe = await ffprobe.probeAsync(path, attemptDirectory, cancellationToken); // характеристики медиафайла
            // собираем DTO файла: полный FilePath, размер и характеристики; FileName — имя выдачи, файл на диске не переименовываем
            // Index идет от 0, а FileName из номеров ссылки и файла от 1 в формате 0001-0001.ext
            files.Add(new MediaFileResult {
                Index = fileIndex,
                FileName = $"{urlIndex + 1:D4}-{fileIndex + 1:D4}{extension}",
                SizeBytes = size, DurationSeconds = probe.DurationSeconds, Width = probe.Width, Height = probe.Height,
                FilePath = path, IsCompatibleVideo = probe.IsCompatibleVideo
            });
        }
        return files;
    }

    // проверяем весь список ссылок и возвращаем исходные строки в прежнем порядке, при нарушении выбрасываем BadRequestException
    public List<string> validateRequest(MediaRequest? request) {
        // проверяем наличие DTO, списка и допустимое количество ссылок
        if (request?.Urls is null || request.Urls.Count == 0 || request.Urls.Count > options.MaxUrlsPerBatch) {
            throw new BadRequestException($"Передайте от 1 до {options.MaxUrlsPerBatch} ссылок в массиве urls.");
        }
        List<string> urls = new List<string>(request.Urls.Count); // проверенные строки в порядке входного списка
        foreach (string? value in request.Urls) {
            // проверяем непустую HTTP/HTTPS-ссылку без пробелов, управляющих символов, обратного слеша и логина/пароля в URL
            if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsWhiteSpace) || value.Any(char.IsControl) ||
                value.Contains('\\') || !Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) || string.IsNullOrEmpty(uri.Host) ||
                !string.IsNullOrEmpty(uri.UserInfo)) {
                throw new BadRequestException("Каждый элемент urls должен быть HTTP/HTTPS-ссылкой без учётных данных.");
            }
            // при запрете локальных ссылок проверяем узел через isLocalAddress; DNS и доступность страницы здесь не проверяем
            if (!options.AllowLocalUrls && isLocalAddress(uri)) {
                throw new BadRequestException("Локальные и приватные адреса источников запрещены.", "local_url_forbidden");
            }
            // добавляем исходную строку без изменений: порядок и повторяющиеся ссылки сохраняются
            urls.Add(value);
        }
        return urls;
    }

    // отсеиваем локальные имена и заданные диапазоны IP из URL; фактический IP домена через DNS не определяем
    private static bool isLocalAddress(Uri uri) {
        // убираем точку в конце имени и скобки IPv6, затем проверяем localhost, .localhost и .local
        string host = uri.IdnHost.TrimEnd('.').Trim('[', ']');
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)) {
            return true;
        }
        // если узел не записан как IP и не отклонен выше, возвращаем false
        if (!IPAddress.TryParse(host, out IPAddress? address)) {
            return false;
        }
        // IPv4 внутри IPv6 приводим к IPv4 для проверки тех же диапазонов
        if (address.IsIPv4MappedToIPv6) {
            address = address.MapToIPv4();
        }
        // отклоняем loopback и неопределенные адреса
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)) {
            return true;
        }
        // по байтам IP проверяем запрещенные диапазоны IPv4
        byte[] bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork) {
            return bytes[0] is 0 or 10 or 127 || bytes[0] >= 224 ||
                (bytes[0] == 100 && bytes[1] is >= 64 and <= 127) ||
                (bytes[0] == 169 && bytes[1] == 254) ||
                (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
                (bytes[0] == 192 && bytes[1] == 168) ||
                (bytes[0] == 198 && bytes[1] is 18 or 19);
        }
        // для IPv6 проверяем локальные, групповые адреса и диапазон fc00::/7 по первому байту
        return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast || (bytes[0] & 0xfe) == 0xfc;
    }

    // при остановке сервера завершаем использование ограничителя скачиваний
    public void Dispose() {
        gate.Dispose();
    }
}
