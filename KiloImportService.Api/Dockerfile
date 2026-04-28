# ────────────── Build stage ──────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0-preview-alpine AS build
WORKDIR /src

# Сначала копируем .csproj для кэша restore
COPY KiloImportService.Api.csproj .
RUN dotnet restore

# Затем — исходники, и публикуем
COPY . .
RUN dotnet publish -c Release -o /app /p:UseAppHost=false

# ────────────── Runtime stage ──────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0-preview-alpine AS runtime
WORKDIR /app
RUN apk add --no-cache curl icu-libs && \
    addgroup -S app && adduser -S app -G app

COPY --from=build /app .
RUN mkdir -p /var/lib/import-files && chown -R app:app /var/lib/import-files /app

USER app
ENV ASPNETCORE_URLS=http://+:5000 \
    DOTNET_GCServer=1 \
    DOTNET_RUNNING_IN_CONTAINER=true

EXPOSE 5000
ENTRYPOINT ["dotnet", "KiloImportService.Api.dll"]
