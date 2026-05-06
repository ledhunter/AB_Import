# ⚠️ Visary DTO: ловушки десериализации

## 📋 Описание

**Статус**: ✅ Документировано
**Дата**: 2026-05-06

Visary возвращает одно и то же поле **разными типами** в зависимости от контекста.
Когда DTO жёстко типизирован (`string?`/`VisaryRef?`/`int?`), любой такой случай ⇒ `JsonException` ⇒ **HTTP 500** на нашей стороне.

В этой сессии нашли и починили **3 таких поля**, всё через переключение на `JsonElement?`:

| Сущность | Поле | Было | Стало | Кейс падения |
|----------|------|------|-------|--------------|
| `OrganizationRaw` | `Status` | `string?` | `JsonElement?` | API возвращает числовой код (`10`) |
| `RoomRaw` | `RoomCategory` | `VisaryRef?` | `JsonElement?` | listview даёт скаляр, crud — объект |
| `ConstructionSiteIndicatorRaw` | `MainSource` | `string?` | `JsonElement?` | то строка, то число |

---

## 🧩 Корневая причина

### 1. Listview ↔ crud отдают разный формат для одного поля

```jsonc
// GET /api/visary/crud/constructionsite/7850 (детальная карточка)
{
  "Region": { "Title": "Москва", "ID": 858, "Hidden": false },  // 👈 объект (VisaryRef)
  "Town":   { "Title": "г.Москва", "ID": 2344, "Hidden": false }
}

// POST /api/visary/listview/constructionsite (список с Columns=[...])
{
  "RegionId": 858,    // 👈 плоский int
  "TownId":   2344
}
```

→ **Один DTO для двух эндпоинтов не работает.** Поэтому у нас:
- `*Raw` — для listview (плоские поля)
- `*Full` — для crud-by-id (вложенные объекты)

### 2. Поле может быть числом, строкой или объектом в зависимости от данных

`MainSource` у `ConstructionSiteIndicator`: если источник — справочник, приходит `int`;
если ручной ввод — приходит `string`. Без `JsonElement?` оба варианта одновременно DTO не покрывает.

### 3. Snapshot ловит только один тип данных

В audit-snapshot `RoomCategory` был `null` → эвристика угадала тип неверно
(см. [53-visary-api-schema-audit.md](./53-visary-api-schema-audit.md)).

---

## ✅ Правильная реализация

### Поля с неоднородным типом → `JsonElement?`

```csharp
public sealed class OrganizationRaw
{
    public int ID { get; set; }
    public string? Title { get; set; }

    // Visary возвращает Status как числовой код. JsonElement? принимает любой реальный
    // тип без падения; разбор — на стороне caller-а.
    public JsonElement? Status { get; set; }
}

public sealed class RoomRaw
{
    public int ID { get; set; }
    // listview даёт скаляр (int), crud — VisaryRef. Поэтому JsonElement?.
    public JsonElement? RoomCategory { get; set; }
}

public sealed class ConstructionSiteIndicatorRaw
{
    public int ID { get; set; }
    // То строка, то число — зависит от типа источника данных.
    public JsonElement? MainSource { get; set; }
}
```

### Caller разбирает `JsonElement` явно

```csharp
foreach (var room in response.Data)
{
    int? categoryId = room.RoomCategory?.ValueKind switch
    {
        JsonValueKind.Number => room.RoomCategory.Value.GetInt32(),
        JsonValueKind.Object => room.RoomCategory.Value.GetProperty("ID").GetInt32(),
        _ => null,
    };
}
```

### Нашли через listview-метод? → проверьте оба формата

Если фикс был сделан в `*Raw` (listview), проверьте — нет ли той же проблемы в `*Full` (crud-by-id):

```bash
grep -E "Status|RoomCategory|MainSource" Visary.Api.Client/Dto/Generated/*.cs
```

В наших фиксах `*Full` уже корректные:
- `OrganizationFull.Status: int?` — для crud-формата это число, ок
- `RoomFull.RoomCategory: int?` — crud отдаёт int, ок
- `ConstructionSiteIndicatorFull.MainSource: JsonElement?` — генератор сразу выбрал `JsonElement?` (snapshot имел null)

### ⚠️ Важно

- **`JsonElement?` — escape hatch**, не «всегда так делать». Если поле гарантированно одного типа — оставляйте конкретный (`int?`, `string?`).
- **Перед фиксом снимите snapshot повторно** на нескольких ID — может тип определится однозначно.
- **Не используйте `object?`** — System.Text.Json десериализует его как `JsonElement` всё равно, но с потерей API.
- **`dynamic` вообще не вариант** — не работает с System.Text.Json без adapter-кода.

---

## ❌ Типичная ошибка №1 — слепое доверие snapshot-у

```csharp
// НЕПРАВИЛЬНО: эвристика угадала по имени поля.
public sealed class RoomFull
{
    public double? Number { get; set; }  // 👈 в snapshot был null, имя оканчивается на "Number"
    // → реально приходит string "5/А" — упадёт.
}
```

**Правильно**: для null-полей в snapshot генератор должен ставить `JsonElement?` —
честно «тип неизвестен из примера». См. [scripts/generate-visary-dtos.ps1](../scripts/generate-visary-dtos.ps1):

```powershell
function CSharp-Type([string] $f, [string] $dt) {
    switch ($dt) {
        # ...
        'null' { return 'JsonElement?' }   # 👈 не угадываем
    }
}
```

## ❌ Типичная ошибка №2 — один DTO для listview и crud

```csharp
// НЕПРАВИЛЬНО: пытаемся одним DTO покрыть оба формата.
public sealed class ConstructionSite
{
    public VisaryRef? Region { get; set; }  // listview не даст объект
    public int? RegionId { get; set; }      // crud не даст плоский id
    // → одна половина свойств всегда null. Caller-у непонятно, что использовать.
}
```

**Правильно** — два DTO с явным назначением:
- `ConstructionSiteRaw` (listview, ~12 плоских полей)
- `ConstructionSiteFull` (crud-by-id, ~81 поле с вложенными объектами)

## ❌ Типичная ошибка №3 — забытые using-ы для JsonElement

```csharp
namespace Visary.Api.Dto;
// 👈 нет 'using System.Text.Json;'

public sealed class RoomRaw
{
    public JsonElement? RoomCategory { get; set; }  // 👈 не скомпилируется
}
```

---

## 📍 Применение в проекте

| Файл | Что содержит |
|------|--------------|
| [Visary.Api.Client/Dto/VisaryEntities.cs](../Visary.Api.Client/Dto/VisaryEntities.cs) | `*Raw` DTO для listview (с `JsonElement?` для проблемных полей) |
| [Visary.Api.Client/Dto/Generated/](../Visary.Api.Client/Dto/Generated/) | `*Full` DTO для crud-by-id, генерируются из `visary_api.fields` |
| [scripts/generate-visary-dtos.ps1](../scripts/generate-visary-dtos.ps1) | Генератор: `null`-поля → `JsonElement?` (без угадывания) |

---

## 🎯 Чек-лист при добавлении/изменении DTO

- [ ] Снять snapshot для нескольких разных экземпляров сущности (один может содержать null)
- [ ] Если поле в snapshot — `null`: использовать `JsonElement?`
- [ ] Если поле в listview скаляр, в crud объект: использовать `JsonElement?` в `*Raw`
- [ ] Прогнать live-тест: `dotnet test --filter "Category=live&FullyQualifiedName~ListView"`
- [ ] Если падает — снять capture в `visary_api.captures.response_body`, посмотреть реальный JSON

---

## 🔍 Как найти все потенциально проблемные поля

```sql
-- Поля типа 'null' в snapshot — кандидаты на JsonElement?
SELECT mnemonic, path
FROM visary_api.fields
WHERE location = 'response_body' AND data_type = 'null';

-- Поля, которые у одной сущности 'ref', но потенциально приходят и скаляром
-- (нужна ручная проверка через listview).
SELECT mnemonic, path FROM visary_api.fields
WHERE data_type = 'ref' AND path NOT LIKE '%.%';
```

См. также: [53-visary-api-schema-audit.md](./53-visary-api-schema-audit.md) (snapshot-аудит API), [55-visary-proxy-controllers.md](./55-visary-proxy-controllers.md) (контроллеры).
