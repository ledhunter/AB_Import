# 🔌 Динамический справочник «Тип отделки» (вместо хардкода)

## 📋 Описание

**Статус**: ✅ Реализовано
**Дата**: 2026-05-06

Маппер `FinModelImportMapper` раньше переводил название типа отделки в ID
**жёстким switch'ем**:

```csharp
// БЫЛО — хардкод
return title switch {
    "Черновая"     => 3,
    "Предчистовая" => 2,
    "Чистовая"     => 1,
    _              => null
};
```

Проблемы:
- IDs могут расходиться между средами (test / prod) или меняться.
- Любой новый тип отделки в Visary (например, «Без отделки» с ID=4) — приходится
  патчить код и катить релиз.
- Сообщение об ошибке `Допустимые: Черновая, Предчистовая, Чистовая` врёт
  пользователю, как только справочник в Visary обновился.

Теперь маппер тянет справочник из `IListViewClient.GetFinishingMaterialsAsync`
один раз на сессию и резолвит `Title → ID` по живым данным.

> 🔁 См. также: `50-visary-api-new-methods.md` (общий паттерн методов клиента),
> `10-listview-library.md` (3-шаговое добавление listview-метода).

---

## ✅ Правильная реализация

### 1. DTO в общей папке `Visary.Api.Client/Dto/`

```csharp
// Visary.Api.Client/Dto/VisaryDtos.cs
public sealed class FinishingMaterialRaw
{
    public int ID { get; set; }
    public string? Title { get; set; }
    public string? Code { get; set; }
    public double? Ration { get; set; }
    public int? Status { get; set; }
}
```

### 2. Метод в общем `IListViewClient` (DIM)

```csharp
// Visary.Api.Client/ListView/ListViewClient.cs
public interface IListViewClient : IDisposable
{
    // ...
    Task<ListViewResponse<FinishingMaterialRaw>> GetFinishingMaterialsAsync(
        CancellationToken ct = default)
        => throw new NotImplementedException(nameof(GetFinishingMaterialsAsync));
}

public sealed class ListViewClient : VisaryHttpBase<ListViewClient>, IListViewClient
{
    private static readonly string[] FinishingMaterialColumns =
        ["ID", "Code", "CurrentUser", "Ration", "Title", "Status"];

    public async Task<ListViewResponse<FinishingMaterialRaw>> GetFinishingMaterialsAsync(CancellationToken ct)
    {
        var body = new
        {
            Mnemonic = "finishingmaterial",
            PageSkip = 0,
            PageSize = 50,
            Columns = FinishingMaterialColumns,
            SearchPhrase = (string?)null,
            Sorts = "null",
            Hidden = false,
            Summaries = Array.Empty<object>(),
        };

        return await PostListViewAsync<FinishingMaterialRaw>(
            $"{BaseUrl}/api/visary/listview/finishingmaterial", body, "finishingmaterial", ct);
    }
}
```

### 3. Маппер инжектит клиент и резолвит словарём

```csharp
// Domain/Mapping/FinModelImportMapper.cs
public sealed class FinModelImportMapper : IImportMapper
{
    private readonly ICrudClient _visaryClient;
    private readonly IListViewClient _listViewClient;

    public FinModelImportMapper(
        ILogger<FinModelImportMapper> log,
        ICrudClient visaryClient,
        IListViewClient listViewClient) // ← инжектим
    { ... }

    public async Task<ValidationResult> ValidateAsync(...)
    {
        // Тянем справочник один раз на сессию.
        Dictionary<string, (int Id, string Title)> finishingByTitle;
        try
        {
            var fm = await _listViewClient.GetFinishingMaterialsAsync(ct);
            finishingByTitle = fm.Data
                .Where(m => !string.IsNullOrWhiteSpace(m.Title))
                .ToDictionary(
                    m => m.Title!.Trim(),
                    m => (m.ID, m.Title!.Trim()),
                    StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            // Без справочника валидировать нечем — file-level ошибка, не fallback.
            fileErrors.Add(new RowError(null, "dictionary_unavailable",
                "Не удалось получить справочник «Тип отделки» из Visary: " + ex.Message));
            return new ValidationResult([], fileErrors);
        }

        // ... позже в row-loop:
        if (!finishingByTitle.TryGetValue(value, out var entry))
        {
            var allowed = string.Join(", ", finishingByTitle.Values.Select(v => v.Title));
            rowErrors.Add(new RowError(col, "invalid_value",
                $"Неизвестный тип отделки: '{value}'. Допустимые: {allowed}.")); // ← живой список
        }
    }
}
```

### ⚠️ Важно

- **Справочник тянем один раз на сессию** (в начале `ValidateAsync`), не на каждую строку. На больших файлах (тысячи строк) per-row HTTP-вызовы убьют производительность.
- **Нет hardcoded fallback'а.** Если Visary недоступен — `file-level dictionary_unavailable`. Молча подставлять старые IDs нельзя: это чревато записью неправильных значений в БД.
- **Сообщение об ошибке использует `Title`-ы из живого справочника**, а не статичную строку. Пользователь увидит реально допустимые значения, а не выдумку из времён первого релиза.
- **Lookup case-insensitive** (`StringComparer.OrdinalIgnoreCase`): «Черновая», «черновая», «ЧЕРНОВАЯ» из Excel должны мапиться одинаково.
- **Метод и DTO живут в `Visary.Api.Client`** — переиспользуемое ядро (см. `50-visary-api-new-methods.md`). Любой будущий импорт инжектит тот же `IListViewClient` и переиспользует метод. Нельзя писать собственный HTTP-вызов в маппере.
- **DIM-default `=> throw new NotImplementedException()`** — существующие fake/моки `IListViewClient` в чужих тестах не сломаются: им не нужно реализовывать новый метод.

---

## ❌ Типичная ошибка

### 1. Хардкод switch'ем

```csharp
// ❌ Захардкожено
private static int? GetFinishingMaterialId(string title) => title switch
{
    "Черновая"     => 3,
    "Предчистовая" => 2,
    "Чистовая"     => 1,
    _              => null
};
```

ID живут на стороне Visary, у них нет контракта стабильности. На prod-среде
«Черновая» легко может оказаться ID=7, а не 3 — мы запишем в БД мусор.

### 2. Fallback на хардкод при недоступности Visary

```csharp
// ❌ Молчаливый fallback — пишем неправильные ID в БД
try {
    finishingByTitle = await FetchFromVisary(ct);
} catch {
    finishingByTitle = HardcodedDefaults; // ← опасно!
}
```

Если Visary упадёт во время большого импорта, мы тихо запишем устаревшие IDs.
Лучше **остановить импорт с понятной ошибкой**, пользователь повторит после восстановления.

### 3. HTTP-вызов в маппере вручную

```csharp
// ❌ Дублирование HTTP-обвязки
private async Task<Dictionary<...>> FetchAsync(CancellationToken ct)
{
    using var http = new HttpClient();   // антипаттерн (см. doc 51)
    var resp = await http.PostAsync("https://isup-alfa-test.k8s.npc.ba/api/visary/listview/finishingmaterial", ...);
    // ... парсинг руками
}
```

Не получает Bearer-токен из конфига, обходит `IHttpClientFactory`, ломается в Docker (см. `51-sites-sync-bugs-and-token-update.md`).
**Правильно**: метод в `IListViewClient`, инжект через DI, использует общий `PostListViewAsync<T>` и `VisaryHttpBase<T>`.

### 4. Тянуть справочник на каждую строку

```csharp
// ❌ N+1 HTTP-вызовов
for (int i = 0; i < rows.Count; i++)
{
    var fm = await _listViewClient.GetFinishingMaterialsAsync(ct); // на каждую!
    // ...
}
```

На 2782 строки — 2782 HTTP-вызова к Visary. Кэшируем один раз в начале `ValidateAsync`.

---

## 📍 Применение в проекте

| Компонент | Файл | Что добавлено |
|-----------|------|---------------|
| DTO | [Visary.Api.Client/Dto/VisaryDtos.cs](../Visary.Api.Client/Dto/VisaryDtos.cs) | `FinishingMaterialRaw` |
| Интерфейс клиента | [Visary.Api.Client/ListView/ListViewClient.cs](../Visary.Api.Client/ListView/ListViewClient.cs) | `IListViewClient.GetFinishingMaterialsAsync()` (DIM) |
| Реализация клиента | там же | Метод + `FinishingMaterialColumns` через `PostListViewAsync` |
| Потребитель | [Domain/Mapping/FinModelImportMapper.cs](../KiloImportService.Api/Domain/Mapping/FinModelImportMapper.cs) | Инжект `IListViewClient`, dictionary-lookup, удалён `GetFinishingMaterialId` |
| Тест-мок | [KiloImportService.Api.Tests/Mapping/FinModelImportMapperTests.cs](../KiloImportService.Api.Tests/Mapping/FinModelImportMapperTests.cs) | `Mock<IListViewClient>.Setup(GetFinishingMaterialsAsync).ReturnsAsync(...)` |

---

## 🎯 Чек-лист (при добавлении нового справочника Visary в маппер)

- [ ] DTO для записи справочника лежит в `Visary.Api.Client/Dto/VisaryDtos.cs` (или `VisaryEntities.cs`).
- [ ] Метод в `IListViewClient` объявлен через **DIM** (default-throw), реализация в `ListViewClient` через общий `PostListViewAsync<T>`.
- [ ] Колонки запроса — отдельный `private static readonly string[]` рядом с другими `*Columns`.
- [ ] Маппер **инжектит** `IListViewClient`, не создаёт `HttpClient` сам.
- [ ] Справочник тянется **один раз** на `ValidateAsync` (или, если на сессию, кэшируется явно с учётом TTL).
- [ ] При недоступности справочника — **file-level error**, не silent fallback.
- [ ] Сообщения об ошибках валидации показывают **живой список** допустимых значений, не статичную строку.
- [ ] Тесты мокают `Mock<IListViewClient>.Setup(GetXAsync).ReturnsAsync(test_dictionary)`.

---

## 🧪 Связанный паттерн: справочник vs данные

| Тип данных | Где лежат | Как читаем |
|---|---|---|
| **Справочник** (типы отделки, единицы измерения, регионы) | Visary listview | По `Title` через `IListViewClient.GetXAsync()` — динамически, кэш на сессию |
| **Конкретная запись** (Site, Project) | Visary CRUD | По `ID` через `IListViewClient.GetXByIdAsync()` или `GetCrudAsync<T>` |
| **Статус локального процесса** (текущая сессия импорта) | service-db | EF Core напрямую |

Никогда **не путать**: «справочник» в Visary не зашиваем в код, ID конкретной записи можно держать в env только для разовых fixture'ов.

---

**Версия**: 1.0
**Дата**: 2026-05-06
