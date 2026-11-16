FROM mcr.microsoft.com/dotnet/sdk:10.0.301-noble@sha256:ea8bde36c11b6e7eec2656d0e59101d4462f6bd630730f2c8201ed0572b295d5 AS build
WORKDIR /src
COPY MediaDownloader.csproj ./
RUN dotnet restore MediaDownloader.csproj
COPY . ./
RUN dotnet publish MediaDownloader.csproj --configuration Release --no-restore \
    --output /app/publish /p:UseAppHost=false

# This independent target can be checked without compiling application sources.
FROM mcr.microsoft.com/dotnet/aspnet:10.0.9-noble@sha256:7644f992230d35cf230017189d4038c0ae0f7388b13f4f7ae1900a155bafb597 AS media-tools
USER root
RUN apt-get update \
    && apt-get install --yes --no-install-recommends \
        ca-certificates python3 python3-venv ffmpeg=7:6.1.1-3ubuntu5 \
    && rm -rf /var/lib/apt/lists/* \
    && python3 -m venv /opt/media-tools \
    && /opt/media-tools/bin/python -m pip install --no-cache-dir \
        'yt-dlp[default,pin,deno,pin-deno]==2026.10.26' 'gallery-dl==1.32.20' \
    && install -d -o app -g app /app/data/work /home/app/.cache
ENV PATH="/opt/media-tools/bin:${PATH}" \
    PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1 \
    DENO_NO_UPDATE_CHECK=1 \
    ASPNETCORE_HTTP_PORTS=8080 \
    ASPNETCORE_URLS=http://+:8080 \
    Media__WorkingRoot=/app/data/work
WORKDIR /app
USER app
EXPOSE 8080

FROM media-tools AS final
USER root
RUN install -d -o app -g app /app/data/history
USER app
COPY --from=build --chown=app:app /app/publish ./
COPY --chown=app:app LICENSE /app/LICENSE
ENTRYPOINT ["dotnet", "MediaDownloader.dll"]
