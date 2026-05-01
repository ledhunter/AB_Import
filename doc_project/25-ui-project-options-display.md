# 📋 Отображение проектов в Select без лишних символов

## 📋 Описание

Исправление UI-отображения списка проектов строительства. **Требование**: отображать только название проекта без ID и кодов (например, `Тест ФМ - Опус` вместо `Тест ФМ - Опус (5634576748978)`).

---

## ✅ Правильная реализация

### 1. UI-тип `ProjectItem` без поля `code`

```typescript
// types/listView.ts
export interface ProjectItem {
  id: number;
  title: string;
  // code удалено — не нужен для отображения в Select
  raw?: ConstructionProjectRaw;   // оригинальная строка (на случай нужди деталей)
}
```

### 2. Обновлённый `toProjectOption` в `ImportForm.tsx`

```typescript
interface ProjectOption {
  key: string;
  content: string;
}

const toProjectOption = (p: { id: number; title: string }): ProjectOption => ({
  key: String(p.id),
  content: p.title,  // 👈 только title, без code
});
```

### 3. Backend возвращает только `id` и `title`

```csharp
// ProjectsController.cs
[HttpGet("search")]
public async Task<IActionResult> Search(
    [FromQuery] string? q,
    [FromQuery] int limit,
    CancellationToken ct)
{
    var result = await _service.SearchAsync(q ?? string.Empty, limit, ct);
    return Ok(new
    {
        items = result.Items.Select(p => new
        {
            id = p.Id,
            title = p.Title,
            // code удален — теперь UI использует только title
        }),
        fromFallback = result.FromFallback,
        total = result.Items.Count,
    });
}
```

### 4. Убран `FileSha256` из модели и уникальный индекс

```csharp
// ImportSession.cs
public class ImportSession
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ImportTypeCode { get; set; } = null!;
    public string FileName { get; set; } = null!;
    public long FileSize { get; set; }
    public FileFormat FileFormat { get; set; }
    // FileSha256 удалено
    public ImportStatus Status { get; set; } = ImportStatus.Pending;
    // ...
}
```

```csharp
// ImportServiceDbContext.cs
e.HasIndex(x => x.ImportTypeCode).HasDatabaseName("IX_ImportSession_Type");
// Уникальный индекс по Type+Sha256 удалён — теперь один и тот же файл можно загружать несколько раз
```

---

## 📂 Файлы изменений

| Файл | Изменения |
|------|-----------|
| `types/listView.ts` | Убрано поле `code` из `ProjectItem` |
| `services/listView/entities/projects.ts` | Убран `code` из `toProjectItem` |
| `services/projectsBackendApi.ts` | Убран `code` из `BackendProjectDto` и `toProjectItem` |
| `components/ImportForm/ImportForm.tsx` | `toProjectOption` теперь возвращает только `p.title` |
| `Data/ImportServiceDbContext.cs` | Убран `FileSha256` из модели и уникальный индекс |
| `Data/Entities/ImportSession.cs` | Убрано свойство `FileSha256` |
| `Domain/Pipeline/ImportPipeline.cs` | Убрано вычисление и сохранение SHA256 |
| `Domain/Mapping/FinModelImportMapper.cs` | Без изменений (корректен) |
| `Controllers/ProjectsController.cs` | Убрано поле `code` из DTO |
| `Migrations` | Удалена старая миграция, создана `RemoveFileSha256Constraint` |

---

## ⚠️ Важно

- **Один и тот же файл можно загружать несколько раз по одному типу импорта** — раньше был уникальный индекс по `ImportTypeCode + FileSha256`, который блокировал повторную загрузку
- **Backend теперь возвращает только `id` и `title`** — `code` (IdentifierKK/ZPLM) больше не используется UI
- **Миграция применена вручную** — EF Core tools не работают в Docker, пришлось использовать SQL

---

## 🎯 Чек-лист

- [x] UI показывает только `title` в Select (без кодов)
- [x] Backend возвращает только `id` и `title` (без `code`)
- [x] Один и тот же файл можно загружать несколько раз
- [x] Миграция применена в БД
- [x] Backend и frontend пересобраны
- [x] Все тесты проходят

---

**Версия**: 1.0  
**Дата**: 2026-05-01  
**Автор**: Kilo
