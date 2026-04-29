# 🐛 .NET 10 + Docker + Alpine: 5 граблей при первом деплое

## 📋 Описание

Когда мы впервые подняли проект через `docker compose up -d --build` после миграции на .NET 10, поймали **пять разных проблем подряд**. Все связаны с тем, что .NET 10 GA + Alpine-образ + Linux-runtime ведут себя иначе, чем .NET 8 на Windows. Документ — чек-лист, чтобы в следующий раз не залипнуть на каждой по 10 минут.

> 🔁 См. также: `08-visary-api-integration.md`, `12-ef-core-migrations.md`, `17-backend-tests-xunit.md`, `18-projects-cache.md`.

---

## ❌ #1: `addgroup: group 'app' in use` в Dockerfile

### Симптом
```
ERROR: process "/bin/sh -c apk add --no-cache curl icu-libs &&     addgroup -S app && adduser -S app -G app"
       did not complete successfully: exit code: 1
addgroup: group 'app' in use
```

### Причина
В свежем `mcr.microsoft.com/dotnet/aspnet:10.0-preview-alpine` группа `app` **уже создана** базовым образом. `addgroup -S app` падает, и сборка прерывается.

### ✅ Правильно
```dockerfile
# КОРРЕКТНО — идемпотентное создание
RUN apk add --no-cache curl icu-libs && \
    (getent group app  >/dev/null || addgroup -S app) && \
    (getent passwd app >/dev/null || adduser  -S app -G app)
```

### ❌ Неправильно
```dockerfile
# НЕПРАВИЛЬНО — упадёт на новых базовых образах
RUN apk add --no-cache curl icu-libs && \
    addgroup -S app && adduser -S app -G app
```

📍 `@KiloImportService.Api/Dockerfile:18-20`.

---

## ❌ #2: `Could not load type 'OperationType' from 'Microsoft.OpenApi'`

### Симптом
```
System.Reflection.ReflectionTypeLoadException: Unable to load one or more of the requested types.
Could not load type 'Microsoft.OpenApi.Models.OperationType' from assembly 'Microsoft.OpenApi, Version=2.0.0.0'
```
Backend контейнер начинает crash-loop, никаких контроллеров не загружает.

### Причина
- `Swashbuckle.AspNetCore 7.x` зависит от `Microsoft.OpenApi 1.6.x`, где `OperationType` — концретный enum.
- `.NET 10` подтягивает `Microsoft.OpenApi 2.x` (для нативного `Microsoft.AspNetCore.OpenApi`), где этот enum **удалён** (там переход на интерфейсы и OpenAPI 3.1).
- Старый Swashbuckle reflection'ом ищет тип, не находит — fail at startup.

### ✅ Правильно
```xml
<!-- КОРРЕКТНО — Swashbuckle 10.1.7+ совместим с OpenApi 2.x -->
<PackageReference Include="Microsoft.AspNetCore.OpenApi" Version="10.0.7" />
<PackageReference Include="Swashbuckle.AspNetCore" Version="10.1.7" />
<PackageReference Include="Swashbuckle.AspNetCore.Annotations" Version="10.1.7" />
```

### ❌ Неправильно
```xml
<!-- НЕПРАВИЛЬНО — несовместимая комбинация -->
<PackageReference Include="Microsoft.AspNetCore.OpenApi" Version="10.0.0-preview.4.25258.110" />
<PackageReference Include="Swashbuckle.AspNetCore" Version="7.2.0" />
```

📍 `@KiloImportService.Api/KiloImportService.Api.csproj:15-19`.

---

## ❌ #3: `JsonDocument` property не маппится в InMemory тестах

### Симптом
```
System.InvalidOperationException : The 'JsonDocument' property 'StagedRow.MappedValues'
could not be mapped because the database provider does not support this type.
```
58 backend xUnit-тестов падают разом, хотя в коде `OnModelCreating` указано `HasColumnType("jsonb")`.

### Причина
Npgsql нативно поддерживает `JsonDocument ↔ jsonb`, но **InMemory-провайдер** (используется в тестах) не знает, что делать с `JsonDocument` — нужен value-конвертер.

### ✅ Правильно — провайдер-зависимая конфигурация

```csharp
// @KiloImportService.Api/Data/ImportServiceDbContext.cs
b.Entity<StagedRow>(e =>
{
    if (Database.IsNpgsql())
    {
        // Npgsql сам мапит JsonDocument в jsonb.
        e.Property(x => x.RawValues).HasColumnType("jsonb").IsRequired();
        e.Property(x => x.MappedValues).HasColumnType("jsonb");
    }
    else
    {
        // InMemory / Sqlite — конвертируем в строку (только для тестов).
        e.Property(x => x.RawValues).HasConversion(JsonDocConverter).IsRequired();
        e.Property(x => x.MappedValues).HasConversion(JsonDocConverter);
    }
});

private static readonly ValueConverter<JsonDocument, string> JsonDocConverter =
    new(
        v => v == null ? "{}" : v.RootElement.GetRawText(),
        v => JsonDocument.Parse(string.IsNullOrWhiteSpace(v) ? "{}" : v, default));
```

### ❌ Неправильно — глобальный конвертер
```csharp
// НЕПРАВИЛЬНО — обнулит нативный jsonb-маппинг для Npgsql
modelBuilder.Properties<JsonDocument>().HaveConversion<string>();
```

### ⚠️ Альтернатива
Использовать **отдельный** test DbContext, унаследованный от прод-контекста и переопределяющий `OnModelCreating` для InMemory. Дороже в поддержке.

📍 `@KiloImportService.Api/Data/ImportServiceDbContext.cs:71-99,154-157`.

---

## ❌ #4: TLS handshake падает в Alpine-контейнере

### Симптом
```
HttpRequestException: The SSL connection could not be established
  ---> AuthenticationException: The remote certificate is invalid because of errors in the certificate chain:
       RevocationStatusUnknown, OfflineRevocation
```
При этом `openssl s_client` из этого же контейнера к тому же хосту работает.

### Причина
- .NET runtime на Linux пытается **online-проверить статус отзыва** TLS-сертификата (CRL/OCSP).
- В Alpine минимальные библиотеки и/или закрытые egress-правила к OCSP responder'ам — проверка падает.
- `RevocationStatusUnknown` плюс `OfflineRevocation` = handshake aborted.

### ✅ Правильно — отключить revocation-чек на dev/test handler'е

```csharp
// @KiloImportService.Api/Program.cs
builder.Services.AddHttpClient<IVisaryListViewClient, VisaryListViewClient>(...)
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        SslOptions = new System.Net.Security.SslClientAuthenticationOptions
        {
            CertificateRevocationCheckMode =
                System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck,
        },
    });
```

### ⚠️ Важно
- Это **dev-конфигурация**. В production с настроенным OCSP responder'ом стоит вернуть `Online` или хотя бы `Offline` (с CRL-кэшем).
- Цепочка сертификата всё равно валидируется — отключается только проверка **отзыва**.

### ❌ НЕ делай так
```csharp
// НЕПРАВИЛЬНО — отключает ВСЮ валидацию сертификата
ServerCertificateCustomValidationCallback = (_, _, _, _) => true;
```

📍 `@KiloImportService.Api/Program.cs:54-69`.

---

## ❌ #5: `value too long for type character varying(64)`

### Симптом
```
22001: value too long for type character varying(64)
   File: varchar.c
   Line: 638
DbUpdateException: An error occurred while saving the entity changes.
```
Sync с Visary упал на середине — первый `SaveChanges` положил часть страниц, потом строка с длинным `IdentifierKK` сломала транзакцию.

### Причина
В первой миграции (написанной руками) колонки `IdentifierKK`/`IdentifierZPLM` сделаны `varchar(64)`. На реальных данных Visary встречаются коды длиной >64 символов.

### ✅ Правильно
В сущности и в миграции — **не жадничать** на этих полях, особенно если данные приходят из внешней системы:

```csharp
// @KiloImportService.Api/Data/ImportServiceDbContext.cs
e.Property(x => x.IdentifierKK).HasMaxLength(255);
e.Property(x => x.IdentifierZPLM).HasMaxLength(255);
```

Если миграция уже применена — ALTER:

```sql
ALTER TABLE import.cached_projects
  ALTER COLUMN "IdentifierKK"   TYPE varchar(255),
  ALTER COLUMN "IdentifierZPLM" TYPE varchar(255);
```

### ⚠️ Не забудь синхронизировать
- Сама миграция `20260429170000_AddCachedProjects.cs`.
- Designer.cs миграции.
- `ImportServiceDbContextModelSnapshot.cs`.

📍 `@KiloImportService.Api/Data/ImportServiceDbContext.cs:139-142`, `@KiloImportService.Api/Migrations/20260429170000_AddCachedProjects.cs:20-21`.

---

## 🎯 Чек-лист при подъёме нового .NET 10 + Alpine деплоя

- [ ] `addgroup`/`adduser` обёрнуты в `getent ... ||` для идемпотентности.
- [ ] Swashbuckle ≥ 10.1.7 + `Microsoft.AspNetCore.OpenApi` ≥ 10.0.7 (или вообще без Swashbuckle, если нужен только `MapOpenApi`).
- [ ] `JsonDocument`-поля имеют value-конвертер для не-Npgsql провайдеров (`Database.IsNpgsql()` ветка).
- [ ] HttpClient'ы к внешним API имеют `CertificateRevocationCheckMode = NoCheck` для контейнера.
- [ ] Колонки под внешние идентификаторы (`varchar`) — минимум 255 символов.
- [ ] `.env` для docker-compose с `Visary__BearerToken` присутствует и **не** в Git (см. `.gitignore`).
- [ ] `dotnet test` проходит локально перед `docker compose build`.

---

## 📍 Изменённые файлы

| # | Файл | Что |
|---|------|-----|
| 1 | `@KiloImportService.Api/Dockerfile` | idempotent addgroup |
| 2 | `@KiloImportService.Api/KiloImportService.Api.csproj` | Swashbuckle 10.1.7, OpenApi 10.0.7 |
| 3 | `@KiloImportService.Api/Data/ImportServiceDbContext.cs` | JsonDoc конвертер для не-Npgsql |
| 4 | `@KiloImportService.Api/Program.cs` | HttpClient SslOptions.NoCheck |
| 5 | `@KiloImportService.Api/Data/ImportServiceDbContext.cs` + миграция | varchar(255) для KK/ZPLM |
| – | `@docker-compose.yml` | проброс `Visary__*` env-переменных |
