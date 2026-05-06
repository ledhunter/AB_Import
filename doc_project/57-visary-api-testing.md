# 🧪 Тестирование Visary API: три уровня

## 📋 Описание

**Статус**: ✅ Реализовано
**Дата**: 2026-05-06
**Покрытие**: 162 теста (124 unit + 38 live)

Три независимых уровня тестов, по возрастанию «дороговизны» прогона:

| Уровень | Кол-во | Скорость | Зависимость от Visary | Использование |
|---------|--------|----------|----------------------|---------------|
| **Контракт-тесты клиентов** | 39 | ~1 сек | нет (mock HttpClient) | каждый PR, CI |
| **Тесты контроллеров** | 18 | ~1 сек | нет (Moq на клиентов) | каждый PR, CI |
| **Live smoke-тесты** | 38 | ~45 сек | да (реальный API + токен) | nightly или вручную |

Цель — **поймать любую регрессию URL/тела/типа DTO до того, как она попадёт в прод**.

---

## ✅ Уровень 1: контракт-тесты клиентов

### Что проверяем

- HTTP-метод (`GET`/`POST`/`PATCH`)
- URL (включая query params и `associationId`)
- Заголовок `Authorization: Bearer ...`
- Тело JSON (структура, экранирование значений в `Filter`)
- Корректность валидации (например, `request.ID` vs route `id`)

### Фикстура

```csharp
public sealed class RecordingHttpHandler : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = new();
    public List<string?> RequestBodies { get; } = new();
    private readonly Queue<HttpResponseMessage> _responses = new();

    public RecordingHttpHandler EnqueueJson(string json) { ... }

    protected override async Task<HttpResponseMessage> SendAsync(...)
    {
        Requests.Add(request);
        RequestBodies.Add(await request.Content?.ReadAsStringAsync(ct));
        return _responses.Count > 0 ? _responses.Dequeue() : new(...);
    }
}
```

### Пример теста

```csharp
[Fact]
public async Task PatchSiteAsync_sends_PATCH_with_id_in_route_and_body()
{
    var (client, handler) = TestVisaryClientFactory.NewCrud();
    handler.EnqueueJson("{}");

    await client.PatchSiteAsync(123, new SitePatchRequest { RowVersion = 42 }, default);

    var req = Assert.Single(handler.Requests);
    Assert.Equal(HttpMethod.Patch, req.Method);
    Assert.Equal($"{Base}/api/visary/crud/constructionsite/123?forceUpdate=false",
                 req.RequestUri!.ToString());
    Assert.Contains("\"ID\":123", handler.RequestBodies[0]);
}
```

### Защита от инъекции в фильтрах

```csharp
[Theory]
[InlineData("Title", "with \"quote\"", "[\"Title\",\"=\",\"with \\u0022quote\\u0022\"]")]
public async Task FilterByString_escapes_value_safely(string field, string value, string expectedJson)
{
    // ...
    var filter = doc.RootElement.GetProperty("Filter").GetString();
    Assert.Equal(expectedJson, filter);  // 👈 кавычки внутри значения экранированы
}
```

### ⚠️ Важно

- **`MockBehavior.Strict`** в Moq для контроллеров — упадёт, если вызвали неожиданный метод.
- **`[Theory]` с `[InlineData]`** для каждой мнемоники — компактно покрывает 19 сущностей.
- **`default` для CancellationToken** — в реализации опциональных параметров нет, явно передавайте.

---

## ✅ Уровень 2: тесты контроллеров

### Что проверяем

- Каждый action делегирует в правильный метод клиента
- Query-параметры пробрасываются корректно
- `BadRequest` при невалидных запросах (без siteId/sectionId у `/rooms`)
- Registry корректно резолвит справочники по имени
- 404 со списком доступных при неизвестном имени

### Пример: action делегирует в правильный метод

```csharp
[Fact]
public async Task ListRooms_with_siteId_calls_GetRoomsBySiteAsync()
{
    var (c, lv, _) = NewController();
    lv.Setup(x => x.GetRoomsBySiteAsync(7850, "u-1", default))
      .ReturnsAsync(EmptyList<RoomRaw>());

    var result = await c.ListRooms(siteId: 7850, sectionId: null,
                                   uniqueNumberFilter: "u-1", default);

    lv.VerifyAll();  // 👈 если controller не вызвал GetRoomsBySiteAsync — упадёт
    Assert.IsType<OkObjectResult>(result);
}
```

### Пример: registry для справочников

```csharp
[Fact]
public async Task List_unknown_dictionary_returns_404_with_available_names()
{
    var registry = NewRegistry(("towns", new StubHandler()), ("regions", new StubHandler()));

    var result = await NewController(registry).List("nonexistent", titleFilter: null, default);

    var nf = Assert.IsType<NotFoundObjectResult>(result);
    var json = JsonSerializer.Serialize(nf.Value);
    Assert.Contains("towns", json);    // available перечисляет зарегистрированные
    Assert.Contains("regions", json);
}
```

---

## ✅ Уровень 3: live smoke-тесты

### Что проверяем (главное!)

**Десериализация реального ответа Visary в наши DTO без падения**.
Это поймало бы регрессии `MainSource`/`RoomCategory`/`Status`, описанные в [56-visary-dto-deserialization-pitfalls.md](./56-visary-dto-deserialization-pitfalls.md).

### Резолвер токена и BaseUrl

Источники в порядке приоритета:
1. **env**: `VISARY_TEST_TOKEN`, `VISARY_TEST_BASEURL` — для CI с секретами
2. **`.audit/.token`** — для audit-скриптов
3. **`appsettings.Local.json`** — общий с API

```csharp
public static (string? BaseUrl, string? Token) Resolve()
{
    return (
        BaseUrl: envBaseUrl ?? jsonBase ?? DefaultBaseUrl,
        Token:   envToken   ?? fileToken ?? jsonToken
    );
}
```

### Skip на мёртвом токене (а не падение по 401)

```csharp
public static bool IsTokenLikelyAlive(string? jwt)
{
    // Парсим JWT payload, читаем exp, сравниваем с UtcNow + 30 секунд запаса.
    var bytes = Convert.FromBase64String(payload.Replace('-', '+').Replace('_', '/'));
    using var doc = JsonDocument.Parse(bytes);
    var expSeconds = doc.RootElement.GetProperty("exp").GetInt64();
    return DateTimeOffset.FromUnixTimeSeconds(expSeconds) > DateTimeOffset.UtcNow.AddSeconds(30);
}
```

### Пример теста

```csharp
[Trait("Category", "live")]
public sealed class VisaryListViewLiveTests
{
    [SkippableFact]
    public async Task GetRoomsBySiteAsync_deserializes_without_error()
    {
        // Регрессия: тут раньше падало с RoomCategory: VisaryRef vs Number.
        SkipIfNoToken();
        var resp = await VisaryLiveClientFactory.NewListView()
            .GetRoomsBySiteAsync(VisaryLiveTestIds.ConstructionSite, null, default);
        Assert.NotNull(resp);
    }

    private static void SkipIfNoToken()
    {
        var (_, token) = VisaryLiveTestConfig.Resolve();
        Skip.If(string.IsNullOrWhiteSpace(token) ||
                !VisaryLiveTestConfig.IsTokenLikelyAlive(token),
                VisaryLiveTestConfig.SkipReason());
    }
}
```

### Известные ID для test-стенда

```csharp
internal static class VisaryLiveTestIds
{
    public const int ConstructionProject = 4584;
    public const int ConstructionSite    = 7850;
    public const int Room                = 20585;
    // ... 17 ещё
    public const string OrganizationClientId = "2";
}
```

### ⚠️ Важно

- **`[Trait("Category", "live")]`** — фильтр для CI/локального запуска.
- **`Skip.If(...)` (а не `Skippable.If`)** — класс называется `Skip`, без `able`.
- **Не пушать пароли/токены в `appsettings.Local.json` в репо** — он в `.gitignore`, но проверьте `git status` перед коммитом.

---

## ❌ Типичная ошибка №1 — live-тесты как обычные

```csharp
// НЕПРАВИЛЬНО: упадёт в CI без токена.
[Fact]
public async Task GetTownByIdAsync_returns_known_town()
{
    var dto = await client.GetTownByIdAsync(5565);  // 👈 401 в CI = красный билд
}
```

**Правильно** — `[SkippableFact]` + `Skip.If(no_token)`. Тест не упадёт, его просто пропустят.

## ❌ Типичная ошибка №2 — упор только на live-тесты

Live-тесты медленные (~45 сек), требуют живой API и периодически обновляемый токен.
**Без unit-тестов** PR-ревьюер не сможет сразу понять, что новый action делегирует
в правильный метод клиента — он ждёт CI 45 секунд + риск skip из-за токена.

**Правильно** — пирамида: 124 быстрых unit + 38 live.

## ❌ Типичная ошибка №3 — `MockBehavior.Loose`

```csharp
// НЕПРАВИЛЬНО: тест проходит, даже если controller вообще не вызвал клиента.
var lv = new Mock<IListViewClient>();  // Loose по умолчанию
// Setup какого-то метода
// Verify не делается — тест зелёный, баг в проде.
```

**Правильно** — `MockBehavior.Strict` + `lv.VerifyAll()` в конце.

---

## 📍 Применение в проекте

| Папка | Что содержит |
|-------|--------------|
| [KiloImportService.Api.Tests/VisaryClients/](../KiloImportService.Api.Tests/VisaryClients/) | Контракт-тесты `CrudClient` и `ListViewClient` + `RecordingHttpHandler` |
| [KiloImportService.Api.Tests/Controllers/](../KiloImportService.Api.Tests/Controllers/) | Тесты `VisaryEntitiesController` и `VisaryDictionariesController` + registry |
| [KiloImportService.Api.Tests/VisaryLive/](../KiloImportService.Api.Tests/VisaryLive/) | Live smoke-тесты, `VisaryLiveTestConfig`, `VisaryLiveTestIds` |

---

## 🎯 Команды

| Цель | Команда |
|------|---------|
| Только unit (без сети) | `dotnet test --filter "Category!=live"` |
| Только live (нужен токен) | `dotnet test --filter "Category=live"` |
| Всё | `dotnet test` |
| Только конкретная сущность live | `dotnet test --filter "Category=live&FullyQualifiedName~Room"` |

---

## 🎯 Чек-лист при добавлении нового метода клиента

- [ ] Контракт-тест в `*ContractTests.cs`: проверить URL + HTTP-метод + тело
- [ ] Если есть `[Theory]` со списком мнемоник — добавить новую в `[InlineData]`
- [ ] Action в контроллере? → тест в `*ControllerTests.cs` с `Mock.Verify`
- [ ] Live-тест в `*LiveTests.cs` с `[SkippableFact]` и `[Trait("Category","live")]`
- [ ] `dotnet test` локально перед PR
- [ ] Прогнать live с актуальным токеном перед merge

См. также: [55-visary-proxy-controllers.md](./55-visary-proxy-controllers.md), [56-visary-dto-deserialization-pitfalls.md](./56-visary-dto-deserialization-pitfalls.md).
