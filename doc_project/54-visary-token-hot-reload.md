# 🔐 Visary Bearer-токен: hot-reload без рестарта

## 📋 Описание

**Статус**: ✅ Реализовано
**Дата**: 2026-05-06

В фазе активного тестирования токен Visary живёт ~1 час и обновляется по нескольку раз за день.
Раньше токен был в `appsettings.json` — попадал в git и требовал перезапуска API.

Текущая схема: токен хранится в **`appsettings.Local.json`** (в `.gitignore`),
HTTP-клиенты Visary читают его через **`IOptionsMonitor<VisaryOptions>`** ⇒
сохранили файл — **следующий запрос идёт с новым токеном без рестарта приложения**.

---

## ✅ Правильная реализация

### `Program.cs` — подключение Local-файла

```csharp
var builder = WebApplication.CreateBuilder(args);

// reloadOnChange:true критично для hot-reload токена.
// optional:true — чтобы прод-окружение могло обходиться без файла.
builder.Configuration.AddJsonFile(
    "appsettings.Local.json", optional: true, reloadOnChange: true);

builder.Services.AddVisaryClient(
    builder.Configuration.GetSection(VisaryOptions.SectionName));
```

### `VisaryHttpBase<T>` — чтение текущего значения на каждый запрос

```csharp
public abstract class VisaryHttpBase<T>
{
    private readonly IOptionsMonitor<VisaryOptions> _optionsMonitor;

    protected VisaryHttpBase(
        HttpClient http,
        IOptionsMonitor<VisaryOptions> optionsMonitor,  // 👈 не IOptions<T>
        ILogger<T> log) { ... }

    // CurrentValue читается на каждый NewRequest — токен всегда свежий.
    protected VisaryOptions Options => _optionsMonitor.CurrentValue;

    protected HttpRequestMessage NewRequest(HttpMethod method, string url)
    {
        EnsureConfig();
        var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Options.BearerToken);
        return req;
    }
}
```

### `appsettings.Local.json` (в `.gitignore`)

```json
{
  "Visary": {
    "BearerToken": "eyJhbGciOiJSUzI1NiIs..."
  }
}
```

### `appsettings.json` (в репозитории) — пустой токен

```json
{
  "Visary": {
    "BaseUrl": "https://isup-alfa-test.k8s.npc.ba",
    "BearerToken": "",
    "SyncPageSize": 200,
    "DefaultPageSize": 50,
    "LargePageSize": 500,
    "RequestTimeout": "00:00:30"
  }
}
```

### ⚠️ Важно

- **`reloadOnChange: true`** — без этого `IOptionsMonitor.CurrentValue` не обновится при правке файла.
- **`IOptionsMonitor<T>`, не `IOptions<T>`** — `IOptions<T>` снимает значение один раз при создании singleton'а HTTP-клиента, изменения в файле игнорируются.
- **`HttpClient` зарегистрирован через `AddHttpClient<I, T>()`** — он singleton, поэтому конструктор клиента вызывается один раз. Если читать токен в конструкторе — он замёрзнет навсегда.
- **Не вызывайте `BuildServiceProvider()` внутри extension-методов** — это анти-паттерн. Используйте `services.AddSingleton<T>(sp => ...)` с lambda для отложенного резолва.

---

## ❌ Типичная ошибка №1 — `IOptions<T>` вместо `IOptionsMonitor<T>`

```csharp
// НЕПРАВИЛЬНО: токен снимается один раз и кэшируется в _options.
public sealed class CrudClient
{
    private readonly VisaryOptions _options;

    public CrudClient(HttpClient http, IOptions<VisaryOptions> options, ...)
    {
        _options = options.Value;  // 👈 снимок на всю жизнь объекта
    }

    private HttpRequestMessage NewRequest(...) =>
        new(...) { Headers = { Authorization = new("Bearer", _options.BearerToken) } };
    // appsettings.Local.json обновлён, но _options.BearerToken тот же → 401.
}
```

## ❌ Типичная ошибка №2 — токен в `appsettings.json`

```json
// НЕПРАВИЛЬНО: попадает в git, остаётся в истории навсегда.
// Даже после удаления — токен скомпрометирован, его нужно отозвать в id-сервере.
{
  "Visary": { "BearerToken": "eyJhbGciOi..." }
}
```

## ❌ Типичная ошибка №3 — `BuildServiceProvider()` в extension

```csharp
// НЕПРАВИЛЬНО: создаёт второй DI-контейнер, теряет singleton-семантику,
// держит ссылки на disposable-сервисы — leak.
public static IServiceCollection AddVisaryClient(this IServiceCollection services, ...)
{
    var sp = services.BuildServiceProvider();  // 👈 анти-паттерн
    var monitor = sp.GetRequiredService<IOptionsMonitor<VisaryOptions>>();
    // ...
}
```

---

## 📍 Применение в проекте

| Файл | Что делает |
|------|------------|
| [KiloImportService.Api/Program.cs](../KiloImportService.Api/Program.cs) | `AddJsonFile("appsettings.Local.json", optional:true, reloadOnChange:true)` |
| [KiloImportService.Api/appsettings.json](../KiloImportService.Api/appsettings.json) | Пустой `BearerToken`, видны все настройки структурно |
| `KiloImportService.Api/appsettings.Local.json` | **В `.gitignore`** — реальный токен живёт здесь |
| [.gitignore](../.gitignore) | `**/appsettings.Local.json` + `.audit/` |
| [Visary.Api.Client/Common/VisaryHttpBase.cs](../Visary.Api.Client/Common/VisaryHttpBase.cs) | Принимает `IOptionsMonitor<VisaryOptions>`, читает `CurrentValue` на каждый запрос |
| [Visary.Api.Client/VisaryClientExtensions.cs](../Visary.Api.Client/VisaryClientExtensions.cs) | `AddVisaryClient(IConfiguration)` для регистрации |

---

## 🎯 Чек-лист обновления токена

- [ ] Открыть DevTools в Visary UI → Network → скопировать `Authorization: Bearer ...`
- [ ] Вставить в `KiloImportService.Api/appsettings.Local.json` поле `Visary.BearerToken`
- [ ] Сохранить файл (Ctrl+S) — **рестарт API не нужен**
- [ ] Следующий запрос автоматически идёт с новым токеном

## 🔄 Альтернативные источники токена для тестов

Live-тесты ([57-visary-api-testing.md](./57-visary-api-testing.md)) ищут токен в порядке приоритета:

1. `VISARY_TEST_TOKEN` (env) — для CI
2. `.audit/.token` (файл) — для audit-скриптов
3. `KiloImportService.Api/appsettings.Local.json` (`Visary:BearerToken`) — общий с API

Если токен не найден или JWT `exp` истёк — live-тесты skip-аются с понятным сообщением.
