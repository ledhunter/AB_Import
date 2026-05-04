# 🧰 Миграция с дубликатов клиентов Visary API в библиотеку

## 📋 Описание

Процесс миграции с внутренних дубликатов клиентов Visary API на переиспользуемую библиотеку `Visary.Api.Client`. Дублирующиеся классы оставались в проекте после рефакторинга, что усложняло поддержку и могло приводить к путанице.

---

## ✅ Правильная реализация

### 1. Удаление дубликатов клиентов

**Последовательность действий:**

1. **Проверка использования** — убедиться, что все компоненты используют `Visary.Api.Client`
   ```csharp
   // Program.cs:18-25
   services
       .AddVisaryClient(opt => { /* options */ })
       .Configure<VisaryOptions>(builder.Configuration.GetSection(VisaryOptions.SectionName));
   ```

2. **Удаление старых классов** — удалить файлы с дублирующей логикой:
   - `KiloImportService.Api/Domain/Visary/VisaryListViewClient.cs` → заменён на `Visary.Api.Client/ListView/ListViewClient.cs`
   - `KiloImportService.Api/Domain/Visary/VisaryApiOptions.cs` → заменён на `Visary.Api.Client/VisaryOptions.cs`
   - `KiloImportService.Api\ KiloImportService.Api/Domain/Visary/VisarySitesCrudClient.cs` → заменён на `Visary.Api.Client/CRUD/CrudClient.cs`

3. **Конфигурация** — убедиться, что конфигурация маппится в `VisaryOptions`:
   ```csharp
   // Program.cs:59-64
   builder.Services
       .AddVisaryClient(opt =>
       {
           opt.BaseUrl = builder.Configuration["Visary:BaseUrl"] ?? string.Empty;
           opt.BearerToken = builder.Configuration["Visary:BearerToken"] ?? string.Empty;
           opt.RequestTimeout = TimeSpan.FromSeconds(30);
       })
       .Configure<VisaryOptions>(builder.Configuration.GetSection(VisaryOptions.SectionName));
   ```

### 2. Обновление интерфейсов

**В проекте используется:**
- `IVisaryClient` → `IVisaryClient` из библиотеки
- `IListViewClient` → `IListViewClient` из библиотеки
- `ICrudClient` → `ICrudClient` из библиотеки

**Совместимость:**
- Интерфейсы из библиотеки имеют те же имена и сигнатуры
- Методы `GetSiteByIdAsync`, `GetSiteByProjectAndIdAsync` возвращают/принимают те же DTO

---

## ❌ Типичная ошибка

### Ошибка 1: Оставление дубликатов после миграции

**Как не делать:**
```csharp
// ❌ НЕПРАВИЛЬНО - использовался устаревший класс
builder.Services.AddScoped<IVisaryListViewClient, VisaryListViewClient>();
```

**Почему это ошибка:**
- Дублирование кода
- Разные пути к конфигурации (`VisaryApiOptions` vs `VisaryOptions`)
- Риск использования устаревшей реализации
- Усложнение поддержки

### Ошибка 2: Неудалённый файл с пробелом в пути

**Как не делать:**
```csharp
// ✅ Файл существует: KiloImportService.Api\ KiloImportService.Api/Domain/Visary/VisarySitesCrudClient.cs
// ❌ Путь содержит пробел после "KiloImportService.Api/"
```

**Почему это ошибка:**
- Некорректный путь в Git (пробел в имени папки)
- Файл дублирует `CrudClient` из библиотеки
- Путаница при поиске файлов

---

## 📍 Применение в проекте

| Компонент | Файл (устаревший) | Файл (актуальный) | Статус |
|-----------|-------------------|-------------------|--------|
| `IListViewClient` | `VisaryListViewClient.cs` | `Visary.Api.Client/ListView/ListViewClient.cs` | ✅ Мигрировано |
| `ICrudClient` | `VisarySitesCrudClient.cs` | `Visary.Api.Client/CRUD/CrudClient.cs` | ✅ Мигрировано |
| `VisaryOptions` | `VisaryApiOptions.cs` | `Visary.Api.Client/VisaryOptions.cs` | ✅ Мигрировано |

### Файлы для удаления

| Файл | Причина удаления |
|------|-----------------|
| `KiloImportService.Api/Domain/Visary/VisaryListViewClient.cs` | Полный дубликат `ListViewClient` |
| `KiloImportService.Api\ KiloImportService.Api/Domain/Visary/VisarySitesCrudClient.cs` | Дубликат `CrudClient` + пробел в пути |
| `KiloImportService.Api/Domain/Visary/VisaryApiOptions.cs` | Полный дубликат `VisaryOptions` |

---

## 🎯 Чек-лист миграции

- [ ] Убедиться, что все компоненты используют `Visary.Api.Client`
- [ ] Проверить `Program.cs` — регистрация `IVisaryClient`, `IListViewClient`, `ICrudClient`
- [ ] Удалить `VisaryListViewClient.cs` и связанные файлы
- [ ] Удалить файл с пробелом в пути (`KiloImportService.Api\ ...`)
- [ ] Запустить тесты — убедиться, что всё работает
- [ ] Обновить документацию (как описано в `.windsurf/workflows/doc.md`)
- [ ] Обновить `INCOMPLETE_PARTS.md` с новым статусом

---

## 🔍 Методика диагностики

Файл составлен на основе анализа:

1. **Поиск дубликатов** — сравнение классов `VisaryListViewClient`/`VisaryApiOptions` с библиотечными аналогами
2. **Анализ использования** — проверка `Program.cs` на регистрацию сервисов
3. **Поиск в файловой системе** — `glob` с поиском файлов с "Probable typo" в путях
4. **Сравнение сигнатур методов** — проверка совместимости интерфейсов

---

**Версия документа**: 1.0  
**Автор**: Kilo  
**Дата создания**: 2026-05-04
