# 🔧 Merge feature-ветки с main: переиспользование общих хелперов

## 📋 Описание

**Статус**: ✅ Применено
**Дата**: 2026-05-06
**Контекст**: при мердже `my-feature-new` → `main` обнаружились конфликты в
`Visary.Api.Client`, потому что параллельно с моей работой в main появилась
переработанная инфраструктура (общие хелперы, auto-generated DTO, mnemonics-константы).

«Конфликт» — это не повод дёргать одну сторону или другую. Это сигнал, что
**обе ветки решали задачу из одной области**. После мерджа моя реализация должна
**использовать общие хелперы из main**, а не дублировать их.

> 🔁 См. также: `50-visary-api-new-methods.md` (общая инфраструктура клиента),
> `56-site-finishing-material-update-crud.md` (что именно было переписано).

---

## ✅ Правильный подход к разрешению конфликтов

### Алгоритм

```
1. git merge origin/main → видим конфликты
2. Для каждого конфликтного файла спросить:
   • Что main добавил/изменил рядом?  (grep по теме конфликта в origin/main)
   • Есть ли в main shared helper, делающий то же, что мой private?
   • Есть ли в main DTO/константа, которую я придумывал заново?
3. Удалить свой дубликат, переписать вызов на использование общего.
4. Если main добавил параллельный API с другим именем (List*Async vs Get*Async)
   — переименовать в потребителях (mapper, тесты, доки), не плодить два API.
5. Обновить доки, упоминающие удалённые сущности.
6. Билд + все тесты → коммит merge с описанием решений по каждому конфликту.
```

### Конкретный пример: «Тип отделки» в main vs my-feature-new

| Что у меня | Что в main | Решение |
|---|---|---|
| `private GetCrudAsync<T>(url, label, ct)` | `protected GetCrudByIdAsync<T>(mnemonic, id, ct)` в `VisaryHttpBase` | Удалил свой private, вызываю общий |
| `private SiteCrudReadResponse { ID, RowVersion }` | `ConstructionSiteFull` в `Dto/Generated/` (есть `RowVersion: long`) | Удалил свой DTO, использую generated |
| `IListViewClient.GetFinishingMaterialsAsync()` (DIM) с собственным body+Columns | `IListViewClient.ListFinishingMaterialsAsync()` через общий `ListDictionaryAsync<T>` (вместе с 7 другими `List*Async`-методами справочников) | Переименовал в потребителях, удалил свой метод |
| `FinishingMaterialRaw` в `Dto/VisaryDtos.cs` | `FinishingMaterialRaw` в `Dto/Generated/FinishingMaterialRaw.cs` (auto-generated, больше полей) | Удалил свой, использую generated |
| URL'ы строкой `"constructionsite"` | Константа `VisaryMnemonics.Site` | Заменил литералы на константу |

После такой консолидации:
- Билд: 0 ошибок
- Тесты: 119/119 passed
- Импорт «Финмодели» прошёл end-to-end в Docker

### ⚠️ Важно

- **Сначала смотри что есть в main**, потом разруливай. Если ткнуть «accept current/incoming» вслепую — получится либо дубликат (все хелперы под двумя именами), либо потеря фикса (мой PATCH-flow заменился бы на легаси PUT).
- **Не оставляй два API с одинаковой семантикой.** `GetXAsync` и `ListXAsync` для одного справочника — путаница для следующего разработчика. Выбери один (тот, что в main, потому что у него больше потребителей) и удали другой.
- **Auto-generated DTO нельзя править руками.** Если main добавил `Dto/Generated/FinishingMaterialRaw.cs`, мой ручной `Dto/VisaryDtos.cs::FinishingMaterialRaw` — дубликат, который не пройдёт сборку (CS0101 «type already defined»).
- **Mnemonics-константы и helpers — единственный источник истины.** В новом коде не вписывай URL `"/api/visary/listview/finishingmaterial"` строкой — есть `VisaryMnemonics.FinishingMaterial` и `ListDictionaryAsync<T>`.
- **Обнови доки в той же сессии**, что и код. Док с устаревшим примером кода хуже, чем отсутствие дока — он уверенно ведёт по тупиковой тропе. Если правишь имя метода/класса — `grep doc_project/ -r 'OldName'` и почини.
- **Сначала разрешай конфликты, потом коммить.** Один merge-коммит с понятным сообщением «что переиспользовали, что выкинули, почему» лучше пяти микро-коммитов «fix conflicts».

---

## ❌ Типичные ошибки

### 1. «Accept current» по всем конфликтам не глядя

```
git checkout --ours Visary.Api.Client/CRUD/CrudClient.cs
git checkout --ours Visary.Api.Client/ListView/ListViewClient.cs
```

**Проблема**: убьёт всю работу main по этому файлу. Останется *только* твой код, без новых GET-методов / mnemonics / helpers, которые добавили в main.

### 2. «Accept incoming» — потеряешь свой фикс

```
git checkout --theirs Visary.Api.Client/CRUD/CrudClient.cs
```

**Проблема**: в нашем кейсе main сохранил **legacy `PutSiteFullAsync`** (PUT через listview, который и возвращал 405). Принять main = вернуть баг.

### 3. Оставить два DTO/метода с одним именем

```csharp
// Visary.Api.Client/Dto/VisaryDtos.cs
public sealed class FinishingMaterialRaw { ... }   // ← мой

// Visary.Api.Client/Dto/Generated/FinishingMaterialRaw.cs
public sealed class FinishingMaterialRaw { ... }   // ← main, auto-generated
```

**Симптом**: `error CS0101: The namespace 'Visary.Api.Dto' already contains a definition for 'FinishingMaterialRaw'`. Ничего не собирается, никаких тестов.

### 4. Закоммитить merge без обновления доков

Доки 56 и 57 содержали примеры кода с моим private `GetCrudAsync<T>` и `SiteCrudReadResponse`, которых больше нет в коде. Следующий разработчик читает док → копирует пример → не компилируется → мат на чате. Если правишь сигнатуру — обновляй и доки.

---

## 📍 Применение в проекте

| Сущность | До merge (моя ветка) | После merge | Файл |
|---|---|---|---|
| GET сущности по ID | private `GetCrudAsync<SiteCrudReadResponse>` | `GetCrudByIdAsync<ConstructionSiteFull>` | [VisaryHttpBase.cs](../Visary.Api.Client/Common/VisaryHttpBase.cs) |
| Справочники | DIM `GetFinishingMaterialsAsync` + кастомный body | `ListFinishingMaterialsAsync` через `ListDictionaryAsync<T>` | [ListViewClient.cs](../Visary.Api.Client/ListView/ListViewClient.cs) |
| DTO справочника | `Dto/VisaryDtos.cs::FinishingMaterialRaw` | `Dto/Generated/FinishingMaterialRaw.cs` (auto) | [Dto/Generated/](../Visary.Api.Client/Dto/Generated/) |
| URL мнемоники | строкой `"constructionsite"` | `VisaryMnemonics.Site` | [VisaryMnemonics.cs](../Visary.Api.Client/Common/VisaryMnemonics.cs) |
| Маппер | `_listViewClient.GetFinishingMaterialsAsync(ct)` | `_listViewClient.ListFinishingMaterialsAsync(ct)` | [FinModelImportMapper.cs](../KiloImportService.Api/Domain/Mapping/FinModelImportMapper.cs) |
| Тест-моки | `Setup(c => c.GetFinishingMaterialsAsync(...))` | `Setup(c => c.ListFinishingMaterialsAsync(...))` | [FinModelImportMapperTests.cs](../KiloImportService.Api.Tests/Mapping/FinModelImportMapperTests.cs) |

---

## 🎯 Чек-лист (при разрешении merge-конфликта в shared-инфраструктуре)

- [ ] Прочитать **обе** стороны конфликта целиком — что и зачем добавляла каждая ветка.
- [ ] `git show origin/main:<file>` — посмотреть полный файл из main, не только конфликтный кусок. Часто рядом уже лежит общий helper, делающий то, что я делал руками.
- [ ] Найти в main shared helper / DTO / константу для своей задачи. Если есть — **удалить свой private/локальный аналог**, переписать вызов.
- [ ] Если main и моя ветка добавили **разные имена для одного API** — выбрать имя из main (там больше потребителей), переименовать у себя.
- [ ] После разрешения конфликтов: `dotnet build && dotnet test` — оба зелёные перед merge-коммитом.
- [ ] Запустить **end-to-end** сценарий моего фикса в Docker — убедиться, что merge ничего не поломал семантически (билд + unit-тесты не покрывают всё).
- [ ] Обновить доки, упоминающие удалённые/переименованные сущности (`grep doc_project/ -r 'OldName'`).
- [ ] Merge-коммит с описанием **по каждому конфликту**: что взяли, что удалили, почему.

---

## 🧪 Связанный паттерн: ветки расходятся быстрее, чем готовится PR

Большие feature-ветки (несколько дней / неделя работы), пока готовится PR, легко отстают от main. Чем дольше ветка живёт — тем сильнее расхождение, тем дороже merge.

| Стратегия | Применять когда |
|---|---|
| **`git merge origin/main` периодически** (раз в день) | Долгая фича, main активно меняется |
| **Rebase перед PR** | Хочется чистую историю; есть уверенность, что соавторы не работают на той же ветке |
| **Сделать ветку короткой (1-2 дня max)** | Идеал; конфликты появляются, но мелкие |

**Анти-паттерн**: «доделаю свою ветку до конца, потом смержу». На длинной ветке это превращается в перепиывание половины кода под изменившийся main.

---

**Версия**: 1.0
**Дата**: 2026-05-06
