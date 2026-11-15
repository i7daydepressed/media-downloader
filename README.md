# media downloader 0.1.0

Сервис ASP.NET Core/.NET 10 скачивает фото, видео и альбомы по ссылкам. HTTP возвращает ZIP; Telegram отправляет видео и документы только в личный чат разрешённого числового ID. Оба адаптера используют общий MediaService.

## Архитектура

На ручку в `MediaController` приходит HTTP-запрос, контроллер вызывает метод `MediaService`, а исключения общий обработчик превращает в HTTP-ответы. В сервисе методы описывают пайплайн бизнес-функции: проверяют вход, оркестрируют вызов тулов (служб) и собирают результат. Telegram-адаптер тоже вызывает этот сервис. Каждая тула делает своё действие:

- `YtDlpClient` — скачивает медиа через yt-dlp.
- `GalleryDlClient` — скачивает через gallery-dl, если yt-dlp не справился.
- `FfmpegClient` — находит FFmpeg для объединения видео и звука при скачивании.
- `FfprobeClient` — читает длительность и размеры медиа.
- `ExternalProcessRunner` — запускает утилиты, следит за таймаутом и отменой.
- `WorkingDirectoryManager` — управляет временными файлами и их очисткой.
- `MediaArchiveWriter` — собирает файлы и отчёт в ZIP для HTTP.

## Запуск

Быстрый запуск из корня проекта: `make` (или `make start`) собирает и запускает приложение с текущим `.env`. Команды `make stop`, `make restart`, `make logs`, `make status` и `make build` останавливают, пересоздают без сборки, показывают логи/статус и собирают образ. Справка: `make help`. Настройки `.env` и тома сохраняются; для Makefile нужен установленный `make`.

Из корня проекта, с Docker и Compose 2.24+. Замените `https://example.com/video` своей ссылкой: это заполнитель, а не поддерживаемый источник.

```sh
docker compose up -d --build app
mkdir -p data/client
curl --fail-with-body -sS http://127.0.0.1:8080/api/media/download \
  -H 'Content-Type: application/json' \
  --data '{"urls":["https://example.com/video"]}' -o data/client/media.zip
```

ZIP содержит файлы и `results.json`: проверяйте `success` для каждой ссылки. HTTP 200 сам по себе не доказывает скачивание. Постоянные ссылки `/api/media/download/links` — заглушка 501. HTTP опубликован на loopback; внешняя публикация не предусмотрена.

Для Telegram создайте `.env` командой `cp -n .env.example .env`, задайте `Telegram__Enabled=true`, собственный `Telegram__BotToken` и положительные ID в `Telegram__AllowedUserIds__0`, `__1` и далее, затем повторите запуск. Пустой whitelist запрещает доступ всем.

Полные настройки, локальный Bot API и ограничения: [usage.md](usage.md).

## Ограничения и риски

В Telegram сейчас приоритет у готового MP4/H.264 для плеера, если такой вариант есть у источника. У него могут быть ниже разрешение и больше размер файла: например, 576×1024 и около 16,6 МБ вместо HEVC 1080×1920 и 12,6 МБ. Сервис скачивает готовый вариант без перекодирования. Если совместимого варианта нет, файл отправляется документом. Настройка переключения между совместимостью и лучшим доступным качеством пока не реализована.

## Происхождение

Подход с внешними медиаинструментами выбран по [Youwee, commit 5fed7acda9c15795224de95582fc2ccd20e91b65](https://github.com/vanloctech/youwee/commit/5fed7acda9c15795224de95582fc2ccd20e91b65). C# CLI-обёртки реализованы в этом проекте: yt-dlp → fallback gallery-dl, FFmpeg/FFprobe; для yt-dlp используются [Deno](https://github.com/denoland/deno/releases) и [EJS](https://github.com/yt-dlp/ejs).

## Актуальность и обновление

Сравнивайте фактические версии с [релизами yt-dlp](https://github.com/yt-dlp/yt-dlp/releases), [gallery-dl на Codeberg](https://codeberg.org/mikf/gallery-dl)/[PyPI](https://pypi.org/project/gallery-dl/) и [FFmpeg](https://ffmpeg.org/download.html). Активная разработка gallery-dl переехала с GitHub на Codeberg: [объявление](https://github.com/mikf/gallery-dl/issues/9374).

```sh
docker compose exec app yt-dlp --version
docker compose exec app gallery-dl --version
docker compose exec app ffmpeg -version
docker compose exec app ffprobe -version
docker compose exec app deno --version
```

Версии закреплены в [Dockerfile](Dockerfile): yt-dlp/gallery-dl, FFmpeg apt-пакет и базовые image tags/digests. EJS/Deno связаны с extras `default,pin,deno,pin-deno` yt-dlp.

Сначала осознанно измените pins; при изменении CLI сверьте `tools/YtDlpClient.cs` и `tools/GalleryDlClient.cs`. Затем пересоберите и примените образ:

```sh
docker compose build --pull app
docker compose up -d app
```

Повторите HTTP-запрос со своей ссылкой и проверьте файлы и `results.json` внутри ZIP. Пересборка, включая `--no-cache`, без изменения pins не обновляет закреплённые версии. Критерий актуальности — успешное реальное скачивание с нужных площадок, а не одна дата релиза.

Код и документы проекта — [MIT](https://opensource.org/license/mit): [LICENSE](LICENSE). Внешние утилиты распространяются под собственными лицензиями.
