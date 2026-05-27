# 🏷 Импорт «Помещения» — дополнительные поля Помещения и ДДУ

## 📋 Описание

В шаблоне «Помещения» появились новые колонки, значения из которых нужно
прокидывать в Visary без поиска перед CREATE/PATCH (по требованию заказчика).
Колонки могут стоять в любом порядке — связь по заголовку.

| Колонка XLSX                                       | Поле Visary                          |
|----------------------------------------------------|--------------------------------------|
| `Вывод (да/нет)` / `Вывод\n(да/нет)` / `Вывод`     | `room.IsWithdrawn`                   |
| `Стоимость ДКП, руб.` / `Сумма депонирования, руб.`| `shareagreement.Cost`                |
| `Сумма на эскроу`                                  | `shareagreement.DepositedAmount`     |
| `Дата ДДУ`                                         | `shareagreement.Date` (ISO `yyyy-MM-dd`) |
| `ФИО покупателя` / `ФИО`                           | `shareagreement.DepositorFullName`   |
| `ПИН застройщика` (уже читается для developer-link)| `shareagreement.DeveloperPIN`        |

Все поля **опциональные** — пустая ячейка → `null` → не попадает в payload
благодаря `JsonIgnoreCondition.WhenWritingNull` HTTP-клиента.

Поиск (find-or-create) **по этим полям не выполняется** — Room/ДДУ
матчатся по существующему бизнес-ключу (см. doc 76, doc 101, doc 106), новые
поля просто отправляются «как есть» в CREATE и в PATCH.

---

## ✅ Правильная реализация

### 1. Алиасы (RoomsFormImportMapper)

ClosedXML отдаёт многострочный заголовок с **реальным `\n`** в `cell.Value`.
`ReadString` сравнивает алиас целиком (без нормализации), поэтому каждую форму
перечисляем явно — с переносом и без:

```csharp
private static readonly string[] IsWithdrawnAliases = [
    "Вывод\n(да/нет)", "Вывод (да/нет)", "Вывод", "IsWithdrawn"];
private static readonly string[] SaCostAliases = [
    "Стоимость ДКП, руб.", "Стоимость ДКП, руб,", "Стоимость ДКП, руб",
    "Сумма депонирования, руб.", "Сумма депонирования, руб",
    "Сумма депонирования", "Cost"];
private static readonly string[] SaDepositedAmountAliases = [
    "Сумма на эскроу", "DepositedAmount"];
private static readonly string[] SaDateAliases = ["Дата ДДУ", "Date"];
private static readonly string[] SaDepositorFullNameAliases = [
    "ФИО покупателя", "ФИО", "DepositorFullName"];
```

### 2. Парсинг

```csharp
bool? isWithdrawn = TryParseBoolYesNo(ReadString(row, IsWithdrawnAliases));

double? saCost      = TryParseNullableDouble(ReadString(row, SaCostAliases), out var saCostErr);
double? saDeposited = TryParseNullableDouble(ReadString(row, SaDepositedAmountAliases), out var saDepErr);
string? saDate      = TryParseExcelDate(ReadString(row, SaDateAliases), out var saDateErr); // ISO yyyy-MM-dd, см. v1.4
var saDepositorFullName = ReadString(row, SaDepositorFullNameAliases);
if (ExcelErrorMarkers.Contains(saDepositorFullName.Trim())) saDepositorFullName = string.Empty;

// ПИН застройщика уже прочитан в developerPin (developer-link flow), переиспользуем.
var saDeveloperPin = developerPin;
```

`TryParseBoolYesNo` — да/нет/yes/no/y/n/true/false/1/0/+/-/—/✓
(case-insensitive, после `Trim`). Неизвестное значение → `null`
(не ошибка): поле опциональное и не блокирующее.

`TryParseExcelDate` — поддерживает:
- **Excel-serial** (число в диапазоне `[1, 80000]`) — конвертируется в ISO
  через `DateTime.FromOADate(serial).ToString("yyyy-MM-dd")`; именно так
  ClosedXML возвращает ячейки `Date`-формата при `cell.GetString()`.
- **Текстовые форматы** (опционально с `HH:mm:ss`):
  `dd.MM.yyyy`, `d.M.yyyy`, `yyyy-MM-dd`, `dd/MM/yyyy`, `d/M/yyyy`,
  `MM/dd/yyyy`, `M/d/yyyy`, `yyyy-MM-ddTHH:mm:ss`, `dd.MM.yyyy HH:mm:ss`,
  `dd/MM/yyyy HH:mm:ss`, `dd/MM/yyyy H:mm:ss`. Текст → `parsed.ToString("yyyy-MM-dd")`.
- Результат — **`string?` (ISO `yyyy-MM-dd`)**, см. `v1.4` ниже.
- Прочерк/`—`/пусто — `null` без ошибки.

### 3. MappedValues

```csharp
var mapped = new Dictionary<string, object?>
{
    // ... существующие поля ...
    ["IsWithdrawn"]                     = isWithdrawn,
    ["ShareAgreementCost"]              = saCost,
    ["ShareAgreementDepositedAmount"]   = saDeposited,
    ["ShareAgreementDate"]              = saDate,
    ["ShareAgreementDepositorFullName"] = saDepositorFullName,
    ["ShareAgreementDeveloperPin"]      = saDeveloperPin,
};
```

### 4. CRUD-вызовы (ApplyAsync)

```csharp
// CREATE / PATCH Room
new RoomCreateRequest {  /* ... */  IsWithdrawn = GetBoolOrNull(v, "IsWithdrawn"), /* ... */ }
new RoomPatchRequest  {  /* ... */  IsWithdrawn = GetBoolOrNull(v, "IsWithdrawn"), /* ... */ }

// CREATE / PATCH ShareAgreement
new ShareAgreementCreateRequest
{
    /* ... */
    Cost              = GetDoubleOrNull(v, "ShareAgreementCost"),
    DepositedAmount   = GetDoubleOrNull(v, "ShareAgreementDepositedAmount"),
    Date              = GetStringOrNull(v, "ShareAgreementDate"),
    DepositorFullName = string.IsNullOrWhiteSpace(saDepositor) ? null : saDepositor,
    DeveloperPIN      = string.IsNullOrWhiteSpace(saDeveloperPin) ? null : saDeveloperPin,
}
```

### 5. HashedMappedFields (RoomApplySnapshotStore)

Все шесть полей включены в `HashedMappedFields` — иначе при первом импорте
без этих колонок и втором уже с ними `diff-skip` решит «без изменений», и
значения никогда не дойдут до Visary.

```csharp
private static readonly string[] HashedMappedFields =
[
    /* ... существующие ... */
    "IsWithdrawn",
    "ShareAgreementCost", "ShareAgreementDepositedAmount",
    "ShareAgreementDate",
    "ShareAgreementDepositorFullName", "ShareAgreementDeveloperPin",
];
```

### 6. DTO (Visary.Api.Client/Dto/VisaryCrudRequests.cs)

Поля nullable; HTTP-клиент сериализуется с `JsonIgnoreCondition.WhenWritingNull`
— `null` не попадает в JSON. Это критично, чтобы старые импорты без новых
колонок не затирали уже-проставленные в Visary значения.

```csharp
public sealed class RoomPatchRequest    { /* ... */ public bool? IsWithdrawn { get; set; } /* ... */ }
public sealed class RoomCreateRequest   { /* ... */ public bool? IsWithdrawn { get; set; } /* ... */ }
public sealed class ShareAgreementPatchRequest
{
    /* ... */
    public double? Cost { get; set; }
    public double? DepositedAmount { get; set; }
    public string? Date { get; set; }                 // doc 113 v1.4: ISO yyyy-MM-dd
    public string? DepositorFullName { get; set; }
    public string? DeveloperPIN { get; set; }
}
// Аналогично в ShareAgreementCreateRequest.
```

---

## 🔁 v1.6 (2026-05-27) — Visary CREATE /room тихо дропает IsWithdrawn → follow-up PATCH после блока ДДУ

**Инцидент:** заказчик прислал GET `/api/visary/crud/room/24899` для комнаты
«Квартира 365_1_» — в ответе `"IsWithdrawn": false`, при том что в файле было
«да» и парсер корректно вычислил `IsWithdrawn=True` (подтверждено
diag-логом `RoomsForm.Apply.Doc113 row=8 roomNumber='365' IsWithdrawn=True`).

**Расследование по логам backend'а:**

```
POST /api/visary/crud/room body=
  {"SiteID":7941, "Title":"Квартира 365_1_", ..., "IsWithdrawn":true, ...}
→ CrudClient.CreateRoomAsync: created id=24899
…
(никаких PATCH /room/24899 — только GET /shareagreement/onetomany/Room
 и PATCH /shareagreement/1301 на привязку орфанного ДДУ)
```

То есть на сторону Visary улетел `"IsWithdrawn":true`, ответ 200 (id=24899),
но GET потом показал `false`. Никакого второго запроса к Room, который бы
сбросил поле, в логе НЕТ. Вывод: **CREATE-эндпоинт Visary принимает
ограниченный набор полей и `IsWithdrawn` в него не входит** — сервер
молча выставляет дефолт `false` независимо от payload.

Параллельно: привязка ДДУ к новой комнате обновляет `ActiveShareAgreement`
на Room (видно в том же GET), то есть Visary всё равно трогает запись
Room после CREATE. Безопаснее зафиксировать `IsWithdrawn` **после** всех
SA-операций, а не сразу за CREATE — это страхует и от CREATE-дропа, и от
потенциального side-effect-а от привязки ДДУ.

**Изменения:**

1. После блока `ShareAgreement find/create/PATCH` добавлен новый шаг
   `(c.1) Финальный PATCH помещения`:

   ```csharp
   if (roomId is int finalRoomId && diagIsWithdrawn is bool isWithdrawnVal)
   {
       await _crud.PatchRoomAsync(finalRoomId, new RoomPatchRequest
       {
           IsWithdrawn = isWithdrawnVal,
       }, gct);
   }
   ```

   `diagIsWithdrawn` — это `GetBoolOrNull(v, "IsWithdrawn")`, вычисленное
   выше для диагностического лога; переиспользуем его (одно чтение
   `MappedValues`).

2. PATCH шлётся **только** при non-null значении (пользователь явно
   указал «да»/«нет» в файле). Пусто → не трогаем, Visary оставит
   собственный дефолт. Накладные расходы — **+1 PATCH на строку с
   заполненным IsWithdrawn**.

3. `IsWithdrawn` НЕ удалён из `RoomCreateRequest`/`RoomPatchRequest` в
   основных вызовах — пусть Visary получает значение и через CREATE на
   случай, если их CREATE-обработчик однажды начнёт принимать поле. Это
   defensive code, а финальный PATCH — гарант.

**Diag-логирование:** `RoomsForm.Apply.Doc113 sheet='…' row=… roomNumber='…'
IsWithdrawn=… ShareAgreementCost=… …` печатается **на каждой строке**
перед CREATE/PATCH Room — теперь видно ровно три точки на одну строку:
(а) что распарсилось из файла; (б) что POST'нулось; (в) уходит ли
follow-up PATCH `IsWithdrawn`.

**Урок:** контракт CREATE и PATCH у Visary НЕ симметричен — некоторые
поля принимает только PATCH (`forceUpdate=true`). Перед добавлением
нового поля в импорт сверять через GET после CREATE, а не доверять,
что POST принял payload «как есть». Применяемая схема: CREATE с
полным payload → SA-операции → PATCH с критичными полями doc 113
(IsWithdrawn). Если в будущем выяснится, что CREATE дропает и
`Cost`/`Date`/`DepositorFullName` — расширить финальный PATCH (но
по текущим логам Cost через SA-PATCH доезжает корректно).

---

## 🔁 v1.5 (2026-05-27) — slash-combined header для Cost

**Инцидент:** реальный шаблон заказчика объединяет в одной ячейке два
альтернативных лейбла через `,/` (запятая-слэш — русская конвенция):

```
"Стоимость ДКП, руб,/Сумма депонирования, руб."
```

`SaCostAliases` содержал каждый лейбл по отдельности, но не объединённую
форму. `ReadString` сравнивает alias целиком (даже после `NormalizeHeader`,
который сворачивает только whitespace, не `/`), поэтому поле молча оставалось
пустым и Visary не получал `ShareAgreement.Cost`. Аналогично могло сломаться
любое будущее поле, чей заголовок заказчик соединит через `,/`.

**Изменения:**

1. В `SaCostAliases` добавлены 4 явные формы slash-combined заголовка
   (с точкой и без, с запятой и без): `"Стоимость ДКП, руб,/Сумма
   депонирования, руб."` и т.п. — на случай ручной правки шаблона.
2. В `ReadString` добавлен **третий fallback** — slash-aware: если cell-key
   содержит `,/`, разбиваем его на сегменты и сравниваем каждый сегмент
   (через `NormalizeHeader`) с alias. **Сегментируем только по `,/`,
   не по голому `/`** — иначе «Вывод (да/нет)» разрывалось бы на «Вывод (да»
   / «нет)» и сломались бы другие алиасы со слэшем внутри парных меток.

**Тест:** `ValidateAsync_SaCost_SlashCombinedHeader_IsMatched` — кладёт в
`ParsedRow.Cells` ключ `"Стоимость ДКП, руб,/Сумма депонирования, руб."` со
значением `1234567` и проверяет, что в `MappedValues.ShareAgreementCost`
оказывается `1234567.0`.

> ⚠️ Если заказчик начнёт объединять заголовки другим разделителем
> (например, `;` или ` / ` с пробелами вокруг) — расширять надо именно
> массив `separator` в slash-aware fallback'е `ReadString`, плюс точечно
> добавлять явные формы в alias-листы (для self-document'ации alias-листа).

---

## 🔁 v1.4 (2026-05-27) — «Дата ДДУ» → ISO-строка (откат v1.1)

**Инцидент:** заказчик прислал реальный payload Visary UI на
`POST /api/visary/crud/shareagreement`:

```json
{ "Project":{"ID":4584}, "Site":{"ID":7849}, "Title":"123", "Number":"123",
  "RoomKindRef":{"ID":3},
  "Date":"2026-05-26",
  "ProjectNumber":"нпс2", "StageNumber":"1", "ConditionalNumber":"1",
  "Cost":1001, "DepositedAmount":1000,
  "DepositorFullName":"фио", "DeveloperPIN":"пин застройщика" }
```

`Date` — **ISO-строка** `"2026-05-26"`, не числовой Excel-serial. v1.1
ошибочно перевела поле на `double?` (Excel-serial 46113), и Visary молча
не сохранял дату (либо отбрасывал, либо принимал как 0).

**Изменения:**

1. `TryParseExcelDate` теперь возвращает `string?` (ISO `yyyy-MM-dd`)
   вместо `double?`. Excel-serial из ячейки → `DateTime.FromOADate(serial)
   .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)`. Текст →
   `parsed.ToString("yyyy-MM-dd")` после `DateTime.TryParseExact`.
2. DTO `ShareAgreementCreateRequest.Date` и `ShareAgreementPatchRequest.Date`:
   `double?` → `string?`.
3. В `Apply` чтение из `MappedValues` — `GetStringOrNull(v, "ShareAgreementDate")`
   (было `GetDoubleOrNull`).
4. `MappedValues["ShareAgreementDate"]` — теперь строка
   (`"2026-04-01"`), а не число. `HashedMappedFields` не тронут
   (имя поля не изменилось, `JsonElement` сам обрабатывает разный тип).

**Тесты:** `TryParseExcelDate_AcceptsKnownTextFormats` сравнивает с ISO-
строкой напрямую (`Assert.Equal("2026-04-01", result)`), `TryParseExcelDate
_07042025_ReturnsCanonicalSerial45754` переименован в `..._ReturnsIsoString`
(ожидание `"2025-04-07"`), `..._AcceptsExcelSerial_ConvertsToIsoString`
проверяет ToOADate→FromOADate round-trip. Apply-тесты переведены на
константу `Doc113ExpectedDateIso = "2026-04-01"`.

> ⚠️ Если Visary в будущем потребует другой формат (например, с временем
> `"2026-05-26T00:00:00"` или с тайм-зоной) — менять надо ровно
> `TryParseExcelDate` и DTO `Date` (всё ещё `string?` — формат произвольный).

---

## 🔁 v1.1 (2026-05-27) — «Дата ДДУ» → Excel-serial (откачено в v1.4)

**Инцидент:** при импорте файла заказчика валидация падала на колонке
«Дата ДДУ» строкой `04/07/2025 00:00:00`:

> `'04/07/2025 00:00:00' не является валидной датой (поддерживаются Excel-serial и форматы dd.MM.yyyy / yyyy-MM-dd).`

ClosedXML для ячейки с явным датным форматом / формулой вернул значение
со временем — формат `dd/MM/yyyy HH:mm:ss` в `TryParseExcelDate` отсутствовал.

**Параллельное требование заказчика:** значение даты передавать в Visary
**числом** (Excel-serial — количество дней от 1899-12-30 base через
`DateTime.ToOADate()`, например `07.04.2025 → 45754`), не ISO-строкой.

**Изменения:**

1. `TryParseExcelDate` теперь возвращает `double?` вместо `string?`:
   - Excel-serial из ячейки пробрасывается **как есть** (был — лишний
     round-trip `FromOADate` → ISO-строка → парсер на следующем слое).
   - Текст → `DateTime.ToOADate()`.
2. Расширен список форматов: добавлены `dd/MM/yyyy HH:mm:ss`,
   `d/M/yyyy HH:mm:ss`, `dd/MM/yyyy H:mm:ss`, `d/M/yyyy H:mm:ss`,
   `dd.MM.yyyy H:mm:ss`, `d.M.yyyy H:mm:ss`, `yyyy-MM-dd H:mm:ss`.
3. `DateTimeStyles.AssumeUniversal | AdjustToUniversal` убран — для
   `ToOADate` Kind не важен, а флаги путали при дальнейшем чтении.
4. DTO `ShareAgreementCreateRequest.Date` и `ShareAgreementPatchRequest.Date`:
   `string?` → `double?`.
5. В `Apply` чтение из `MappedValues` переведено на `GetDoubleOrNull`
   (было `GetStringOrNull`). В `MappedValues` ключ `ShareAgreementDate`
   теперь содержит `double`, а не строку — `HashedMappedFields` не
   тронут (имя поля не изменилось, тип в `JsonElement` обрабатывается).

**Тесты:** добавлены кейсы `04/07/2025 00:00:00`, `4/7/2025 0:00:00`,
`01.04.2026 00:00:00`, `2026-04-01 12:30:45` и регресс
`TryParseExcelDate_07042025_ReturnsCanonicalSerial45754` на пример
заказчика. Apply-тесты переведены на `Doc113ExpectedDateSerial`
(`new DateTime(2026,4,1).ToOADate()`).

> ⚠️ Если в будущем формат `04/07/2025` от заказчика начнёт означать
> **April 7** (US), а не **July 4** (RU) — менять надо именно порядок
> alias-форматов: первый матчинг побеждает. Сейчас `dd/MM/yyyy*` стоит
> раньше любых `MM/dd/yyyy*` — это поведение совпадает с прежней
> семантикой импорта без времени.

---

## 🔁 v1.3 (2026-05-27) — 3 фикса после прод-репорта

**Симптомы** в отчёте импорта (122 строки):
- 22 `apply_failed` «Visary вернул 500 Internal Server Error» в file-level блоке;
- при клике на фильтр «С ошибками 22» — таблица листа пустая («Нет строк»);
- `IsWithdrawn` не выставляется, доп. поля ДДУ (`Cost`/`Date`/`DepositedAmount`/`DepositorFullName`) пустые в Visary.

**Три ортогональные причины — три фикса:**

### 1. `ReadString` не матчил заголовки с whitespace внутри

В alias-листах формы заголовков перечислены явно (`"Вывод\n(да/нет)"`,
`"Вывод (да/нет)"`, `"Вывод"`), но реальный файл от ClosedXML мог отдать
`"Вывод\r\n(да/нет)"`, `"Вывод\t(да/нет)"`, `"Вывод  (да/нет)"` —
ни одна форма не совпала бы.

Старый fallback тримит только края (`.Trim()`), а `\n` посередине
строки оставался. Доп. поля молча оставались `null` → не уходили в Visary.

**Фикс:** новый helper `NormalizeHeader(s)` сворачивает любую
whitespace-последовательность (включая `\n`, `\r\n`, `\t`, NBSP, многократные
пробелы) к одному пробелу и тримит края. Сравнение в fallback идёт по
нормализованной форме с обеих сторон, case-insensitive. Реальный заголовок
любой формы матчится с любой формой alias.

### 2. `apply_failed` падал как file-level error

```csharp
errors.Add(new RowError(null, "apply_failed",
    $"row {mr.SourceRowNumber}: {ex.Message}"));  // SourceRowNumber=null, Sheet=null
```

UI группирует ошибки по `(Sheet, SourceRowNumber)`. Без них ошибка
показывается в блоке «Ошибки уровня файла», а фильтр «С ошибками N» в
таблице листа — пустой. Пользователь не понимает, какие именно строки
упали и на каком листе.

**Фикс:** передаём `SourceRowNumber: mr.SourceRowNumber` и `Sheet: sheetForRow`
(уже доступен из `group.Key.Sheet`) — ошибка попадает в таблицу нужной
строки нужного листа.

### 3. Текст «Visary вернул 500» без body

`VisaryHttpBase.HandleErrorAsync` логировал body, но в `HttpRequestException`
клал только статус + reason — пользователь видел `Visary вернул 500 Internal
Server Error` и не понимал, что именно пошло не так (`required field missing`,
`invalid Date format`, etc.).

**Фикс:** усечённый (до 400 chars, whitespace-collapsed) body прибивается к
тексту исключения через `TruncateForExceptionMessage`. Лог дублирует полный
ответ, в UI попадает короткий хвост. Полная семантика: одинаково помогает
для CREATE Room / PATCH Room / CREATE SA / PATCH SA — это общая обёртка.

### Тесты

- `ValidateAsync_Doc113Headers_WithVariousWhitespace_AreMatched` — 5
  InlineData-кейсов (`\n`, `\r\n`, `\t`, double-space, single-space) —
  каждый матчится с alias `"Вывод (да/нет)"`.
- Остальные тесты doc 113 (Validate/Apply) не тронуты — поведение
  для exact-match формы не изменилось.

---

## 🔁 v1.2 (2026-05-27) — fallback на `MM/dd/yyyy` (US-формат)

**Инцидент:** прод-файл с колонкой «Дата ДДУ» = `11/27/2025 00:00:00`.
v1.1 пыталась распарсить через `dd/MM/yyyy*` (с временем), и **не**
fall-back-ила на `MM/dd/yyyy`. `27` не может быть месяцем → ни один формат
из списка не подходил → row-error на каждой строке.

**Изменение:** в массив `formats` `TryParseExcelDate` добавлены варианты
`MM/dd/yyyy` / `M/d/yyyy` (+ варианты с `HH:mm:ss` и `H:mm:ss`) **после**
`dd/MM/yyyy*`. Поведение:

| Вход               | dd/MM/yyyy сработал? | MM/dd/yyyy fallback? | Итог            |
|--------------------|----------------------|----------------------|-----------------|
| `04/07/2025`       | ✅ d=4, M=7          | (не дошли)           | Jul 4 (как v1.1)|
| `11/27/2025`       | ❌ M=27 invalid      | ✅ M=11, d=27        | Nov 27 (новое)  |
| `13/05/2025`       | ❌ M=13 invalid      | ✅ M=13? — тоже ❌    | row-error       |

Для неоднозначных строк (оба ≤ 12) русская семантика сохраняется
(`dd/MM/yyyy` стоит раньше в массиве — `TryParseExact` берёт первый
совпавший формат). Однозначные US-строки (день > 12) теперь корректно
парсятся в `MM/dd/yyyy`.

**Тесты:** в `TryParseExcelDate_AcceptsKnownTextFormats` добавлены
`InlineData`-кейсы `11/27/2025 00:00:00`, `11/27/2025`, `3/15/2025`.
Текст row-error в `error` обновлён, чтобы перечислить все 4 поддерживаемых
формата (включая `MM/dd/yyyy`).

---

## ❌ Антипаттерны

| Антипаттерн | Чем плох |
|-------------|----------|
| Сравнивать заголовок «нормализованно» (выбросив `\n`) | `ReadString` сравнивает strict — лучше явно держать все формы в alias-листе |
| Делать новые поля частью find-or-create (например, искать ДДУ по `Cost`) | Заказчик: «поиск по новым полям не нужен» |
| Не добавлять поля в `HashedMappedFields` | После первого «пустого» импорта повторный (с заполненными колонками) skip-нётся — данные не уйдут в Visary |
| Слать `IsWithdrawn = false` по умолчанию | Перетирает уже-проставленный признак в Visary при повторном импорте; nullable + `WhenWritingNull` сохраняет old-value |
| Парсить дату через `DateTime.Parse` без `InvariantCulture` | На сервере с другой локалью «01.04.2026» интерпретируется как 1 April vs 4 January |
| Падать с ошибкой при незнакомом значении «Вывод» | Поле опциональное и не блокирующее → возвращаем `null` без `row-error` |
| Слать `Date` в Visary как Excel-serial (v1.1–v1.3) | Visary UI шлёт **ISO-строку** `"2026-05-26"` в `POST /crud/shareagreement` — число не сохраняется/отбрасывается (v1.4 откатил) |
| Менять контракт поля без сравнения с реальным payload UI | v1.1 опиралась на устную инструкцию «храним как Excel-serial»; HAR/payload `POST /crud/shareagreement` показал ISO-строку — нужно было проверить ДО реализации (v1.4) |
| Расширять `TryParseExcelDate` под локальный формат без unit-теста с компонентом времени | Ровно эта дыра дала прод-ошибку «`04/07/2025 00:00:00` не валидная дата» — текущие тесты покрывают только `dd/MM/yyyy` без `HH:mm:ss` (v1.1) |
| Доверять, что `POST /crud/room` принимает все поля из payload | Visary CREATE-эндпоинт **молча дропает** `IsWithdrawn` (v1.6) — `payload IsWithdrawn:true` → GET `IsWithdrawn:false`. Только PATCH с `forceUpdate=true` принимает поле. Контракт CREATE ≠ PATCH у Visary. |
| Делать follow-up PATCH сразу за CREATE (до SA-операций) | Привязка/создание ДДУ может на стороне Visary триггерить пересчёт полей Room (`ActiveShareAgreement` и т.п.) — PATCH ДО SA не страхует от этого, ПОСЛЕ — гарантия (v1.6) |

---

## 🧪 Тесты

| Файл | Что проверяет |
|------|---------------|
| `RoomsFormImportMapperTests.TryParseBoolYesNo_NormalizesYesNoSynonyms` | да/нет/yes/no/+/-/✓/неизвестное → bool?/null |
| `RoomsFormImportMapperTests.TryParseExcelDate_AcceptsKnownTextFormats` | `01.04.2026`, `2026-04-01`, `01/04/2026`, `04/07/2025 00:00:00`, `2026-04-01 12:30:45` → ISO `yyyy-MM-dd` (v1.4) |
| `RoomsFormImportMapperTests.TryParseExcelDate_07042025_ReturnsIsoString` | Пример заказчика: `07.04.2025` → `"2025-04-07"` (v1.4) |
| `RoomsFormImportMapperTests.TryParseExcelDate_AcceptsExcelSerial_ConvertsToIsoString` | Excel-serial из ячейки → ISO `yyyy-MM-dd` через `FromOADate` (v1.4) |
| `RoomsFormImportMapperTests.TryParseExcelDate_EmptyOrDash_IsNullWithoutError` | пусто/`-`/`—` → null без ошибки |
| `RoomsFormImportMapperTests.TryParseExcelDate_InvalidString_ReturnsNullWithError` | «не дата» → null + текст ошибки для row-error |
| `RoomsFormImportMapperApplyTests.ValidateAsync_ReadsDoc113Columns_AndPlacesThemInMappedValues` | Полный pipeline Validate: колонки → MappedValues |
| `RoomsFormImportMapperApplyTests.ApplyAsync_NewRow_WithDoc113Fields_SendsThemToVisaryRoomAndShareAgreement` | CREATE Room/SA несут новые поля в payload |
| `RoomsFormImportMapperApplyTests.ApplyAsync_ExistingRoomAndSa_WithDoc113Fields_PatchesBothEntitiesWithNewValues` | PATCH Room/SA несут новые поля в payload |

---

## 🔗 Связанные документы

- [76 — Find-or-create инвариант для ДДУ](./76-share-agreement-dedup.md) — поиск
  ДДУ ведётся по 5 полям бизнес-ключа, новые поля doc 113 туда **не входят**.
- [96 — Rooms incremental + parallel apply](./96-rooms-incremental-parallel-apply.md) —
  snapshot/diff-skip; `HashedMappedFields` расширен новыми полями.
- [101 — Rooms multi-site by project](./101-rooms-multi-site-by-project.md) —
  per-row резолв SiteId; новые поля живут в той же `MappedValues`.
- [106 — Rooms snapshot revalidation](./106-rooms-snapshot-revalidation.md) —
  ревалидация snapshot против Visary не зависит от новых полей.
- [108 — Rooms studio flag](./108-rooms-studio-flag.md) — паттерн «новое поле
  в `MappedValues` обязано идти и в `HashedMappedFields`».
