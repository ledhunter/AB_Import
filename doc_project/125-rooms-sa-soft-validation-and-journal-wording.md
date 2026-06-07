# 🛡 Импорт «Помещения» — ДДУ не блокирует строку + журнал без тех-сленга

## 📋 Описание

Две связанные правки в `RoomsFormImportMapper` после боевого прогона
Квартирограммы:

1. **ДДУ-валидация не должна блокировать создание/поиск Помещения.**
   Заказчик ставит `-` в «Сумма на эскроу» (или другой пустой маркер),
   когда ДДУ к строке не относится. До правки `TryParseNullableDouble`
   возвращал `invalid_number: '-' не является валидным числом`, строка
   уходила в `IsValid=false`, Apply её пропускал — Помещение не
   создавалось/не находилось, хотя ДДУ объективно не пишется.

2. **Журнал действий — без `PATCH` / `follow-up` / `IsWithdrawn`.**
   Пользователь видит, какие действия применены к Помещению, а не как
   сервис их выполнил. Сообщение «Помещение: IsWithdrawn=False применён
   через follow-up PATCH после привязки ДДУ» дублировалось на каждом
   созданном Помещении и засоряло отчёт.

---

## ✅ Правильная реализация

### 1а. Универсальные «пустые» маркеры в числовом парсере

```csharp
// KiloImportService.Api/Domain/Mapping/RoomsFormImportMapper.cs

private static double? TryParseNullableDouble(string s, out string? error)
{
    error = null;
    if (string.IsNullOrWhiteSpace(s)) return null;
    // 👈 Общепринятые «пустые» маркеры из Excel (`-` / `—` / `–`) — null без ошибки.
    var trimmed = s.Trim();
    if (trimmed == "-" || trimmed == "—" || trimmed == "–") return null;
    if (TryParseDouble(s, out var v)) return v;
    error = $"'{s}' не является валидным числом.";
    return null;
}
```

Те же три маркера уже понимает `TryParseExcelDate` — теперь поведение
одинаковое для чисел и дат.

### 1б. SA-поля (Стоимость / Сумма на эскроу / Дата ДДУ) — soft-валидация

```csharp
// Поля ДДУ НЕ должны блокировать создание/поиск Помещения: если в строке
// нет ДДУ или значение некорректно — просто не пишем поле в Visary,
// строка применяется как обычно. Ошибку парсера логируем в Debug.
double? saCost = TryParseNullableDouble(ReadString(row, SaCostAliases), out var saCostErr);
if (saCostErr != null) _log.LogDebug(
    "RoomsForm.Validate: лист '{Sheet}' стр.{Row} — Стоимость ДДУ '{Err}' (поле пропущено, строка не блокируется).",
    row.Sheet, row.SourceRowNumber, saCostErr);

double? saDeposited = TryParseNullableDouble(ReadString(row, SaDepositedAmountAliases), out var saDepErr);
if (saDepErr != null) _log.LogDebug(/* … */);

string? saDate = TryParseExcelDate(ReadString(row, SaDateAliases), out var saDateErr);
if (saDateErr != null) _log.LogDebug(/* … */);
```

Парс-ошибки **не идут в `rowErrors`** — строка остаётся валидной, Apply
обрабатывает её обычно. Поля Помещения (площадь, кол-во комнат, секция,
этаж и т.п.) продолжают валидироваться строго — там пустой маркер `-`
теперь тоже корректно интерпретируется как «нет значения», но любая
другая опечатка по-прежнему вернёт `invalid_number` и заблокирует строку.

### 2. Журнал действий — никакой метки о «выводе из продажи»

```csharp
if (roomId is int finalRoomId && diagIsWithdrawn is bool isWithdrawnVal)
{
    await _crud.PatchRoomAsync(finalRoomId, new RoomPatchRequest
    {
        IsWithdrawn = isWithdrawnVal,
    }, gct);
    // 👈 В журнал не пишем: признак — поле самого Помещения,
    //    пользователь видит его в Visary напрямую.
}
```

PATCH остаётся (workaround doc 113 — Visary `POST /crud/room` тихо
дропает `IsWithdrawn`, нужен PATCH вслед), но **никакой строки в
отчёте**. Заказчик: «в отчёте вообще не надо ничего писать про вывод из
продажи, даже „Помещение: выведено из продажи“».

### ⚠️ Важно

- **Тех-сленг (`PATCH`, `forceUpdate`, `follow-up`) в journal-метках
  запрещён** — это бизнес-отчёт для пользователя, а не trace backend'а.
- **`IsWithdrawn` в журнал не пишем вообще** — ни для `true`, ни для
  `false`. Это атрибут самого Помещения, не отдельное действие; запись
  «Помещение создано / обновлено» уже покрывает факт записи поля.
- **SA-soft-валидация не маскирует ошибки Помещения**: только три поля
  (`SaCost`, `SaDepositedAmount`, `SaDate`) переведены в soft-режим.
  Парсеры площади, цены, количества комнат и т.п. остаются строгими —
  иначе мы потеряем сигнал об опечатках в данных помещения.
- **Универсальный маркер `-`** распространяется на ВСЕ числовые поля
  (`ProjectArea`, `TotalArea`, `CostForOne`, …). Это согласуется с тем,
  как пользователи Excel обозначают «нет значения», и не маскирует
  опечатки (любой другой не-число всё ещё ошибка).

---

## ❌ Типичная ошибка

```csharp
// НЕПРАВИЛЬНО — превращать любые SA-ошибки в info, забывая универсальный маркер
double? saDeposited = TryParseNullableDouble(...);   // строгий парсер
// и потом просто `try/catch`-подавлять  invalid_number в Apply — слишком поздно.
```

Сначала универсально разрешить «пустые» маркеры в парсере, и только потом
делать soft-режим для SA. Иначе:
- В отчёт по-прежнему текут warning'и об `'-' не является числом`.
- Любая другая числовая колонка с `-` тоже даёт false-positive.

```csharp
// НЕПРАВИЛЬНО — эмитить технические термины в журнал
Log(sheet, row, "Помещение: IsWithdrawn=False применён через follow-up PATCH после привязки ДДУ");
```

`PATCH` / `follow-up` / `IsWithdrawn` — внутренние термины. Пользователь
видит в отчёте каждую строку, и это шум.

```csharp
// НЕПРАВИЛЬНО — глобально подавлять invalid_number у всех числовых полей
if (paErr != null) { /* silently ignore */ }   // ⚠️ Площадь Помещения — обязательное поле
```

Площадь/цена/количество комнат должны блокировать строку при опечатке —
иначе в Visary улетит мусор. Soft-режим — только для SA-полей, которые
влияют только на ДДУ (опциональный объект).

---

## 📍 Применение в проекте

| Компонент | Файл | Сущности |
|-----------|------|----------|
| Числовой парсер | [RoomsFormImportMapper.cs](../KiloImportService.Api/Domain/Mapping/RoomsFormImportMapper.cs) | `TryParseNullableDouble` (универсальный маркер `-`/`—`/`–`) |
| Soft-валидация ДДУ-полей | [RoomsFormImportMapper.cs](../KiloImportService.Api/Domain/Mapping/RoomsFormImportMapper.cs) | блок `saCost`/`saDeposited`/`saDate` в `ValidateAsync` |
| Сообщение об IsWithdrawn | [RoomsFormImportMapper.cs](../KiloImportService.Api/Domain/Mapping/RoomsFormImportMapper.cs) | блок `(c.1) Финальный PATCH помещения (doc 113 workaround)` в Apply |

---

## 🎯 Чек-лист добавления нового опционального поля ДДУ

- [ ] Маппер кладёт значение в JSON без поиска в Visary
- [ ] Парс-ошибка идёт в `_log.LogDebug`, **не** в `rowErrors`
- [ ] Поле добавлено в `HashedMappedFields` (иначе diff-skip ломается, см. doc 113)
- [ ] Visary-payload DTO имеет `JsonIgnore(WhenWritingNull)` для поля
- [ ] Journal-метка (если есть) — на бизнес-языке, без `PATCH`/`forceUpdate`/имён полей DTO
- [ ] Журнал не эмитит метку для дефолтного значения поля
