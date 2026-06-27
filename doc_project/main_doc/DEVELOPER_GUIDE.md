# 🛠️ Гайд по разработке сервиса импорта Visary (Альфа Банк)

## 📋 Содержание

1. [Запуск проекта](#запуск-проекта)
2. [Архитектура](#архитектура)
3. [Как добавить новый тип импорта](#как-добавить-новый-тип-импорта)
4. [Как добавить новый API метод Visary](#как-добавить-новый-api-метод-visary)
5. [Чек-лист перед пулл-реквестом](#чек-лист-перед-пулл-реквестом)

---

## 🚀 Запуск проекта

### Требования
- .NET 10 SDK
- Node.js 20+
- Docker / Docker Compose

### Локальный запуск

```bash
# 1. Запустить PostgreSQL (service-db и visary-db)
docker compose up -d postgresql service-db

# 2. Применить миграции
cd KiloImportService.Api
dotnet ef database update

# 3. Запустить backend
cd KiloImportService.Api
dotnet run

# 4. В другом терминале запустить frontend
cd KiloImportService.Web
npm install
npm run dev
```

### Запуск через Docker Compose

```bash
docker compose up --build
```

---

## 🏗️ Архитектура

### Backend (.NET 10)

```
KiloImportService.Api/
├── Controllers/          # HTTP API endpoints
├── Domain/
│   ├── Importing/       # Пarsers, Mappers, Pipeline
│   ├── Projects/        # Caching logic
│   ├── Sites/           # Sync logic
│   └── Visary/          # Visary CRUD + ListView clients
├── Hubs/               # SignalR
└── Data/               # DbContext + Entities
```

### Frontend (React 19 + TypeScript)

```
KiloImportService.Web/
├── src/
│   ├── components/      # UI components
│   ├── hooks/           # React hooks
│   ├── services/        # API clients + mappers
│   ├── types/           # TypeScript types
│   └── utils/           # Helpers
```

### Ключевые компоненты

| Компонент | Ответственность |
|-----------|----------------|
| `ProjectsCacheService` | Кэш проектов с fallback в Visary |
| `SitesSyncService` | Синхронизация объектов строительства |
| `VisaryListViewClient` | ListView API (generic) |
| `VisaryCrudClient` | CRUD API (update site) |
| `ImportPipeline` | full import cycle |
| `ImportMapperRegistry` | Strategy pattern for mappers |

---

## 📝 Как добавить новый тип импорта

### 1. Backend — Mapper

Создать класс, реализующий `IImportMapper`:

```csharp
public sealed class MyImportMapper : IImportMapper
{
    public string ImportTypeCode => "mytype";

    public async Task<ValidationResult> ValidateAsync(
        ImportContext context,
        IReadOnlyList<ParsedRow> rows,
        VisaryDbContext visaryDb,
        CancellationToken ct)
    {
        // Валидация строк, маппинг в MappedRow
        // return new ValidationResult(mappedRows, fileErrors);
    }

    public async Task<ApplyResult> ApplyAsync(
        ImportContext context,
        VisaryDbContext visaryDb,
        IReadOnlyList<MappedRow> rows,
        CancellationToken ct)
    {
        // Применение изменений к Visary
        // return new ApplyResult(successCount, errors);
    }
}
```

**Зарегистрировать в `Program.cs`:**
```csharp
builder.Services.AddSingleton<IImportMapper, MyImportMapper>();
```

### 2. Frontend — маппер API → UI

Создать маппер в `importMappers.ts`:

```typescript
export const toMyTypeSession = (api: ApiMyTypeSession): UiMyTypeSession => {
  // маппинг полей
};
```

---

## 🔌 Как добавить новый API метод Visary

### 1. Добавить метод в `Visary.Api.Client`

```csharp
// Visary.Api.Client/CRUD/CrudClient.cs
public async Task<MyResult> MyMethodAsync(int id, CancellationToken ct)
{
    // HTTP запрос
}

// Visary.Api.Client/VisaryClientExtensions.cs
services.AddHttpClient<IMyClient, MyClient>((sp, client) => { /* ... */ });
```

### 2. Зарегистрировать в `Program.cs`

```csharp
builder.Services
    .AddVisaryClient(opt => { /* ... */ })
    .Configure<VisaryOptions>(builder.Configuration.GetSection(VisaryOptions.SectionName));
```

---

## ✅ Чек-лист перед пулл-реквестом

### Backend
- [ ] `dotnet test` — все 64 теста проходят
- [ ] `dotnet build` без ошибок
- [ ] Миграции применимы: `dotnet ef database update -p KiloImportService.Api`
- [ ] Docker build проходит: `docker compose build backend`

### Frontend
- [ ] `npm test` — все тесты проходят
- [ ] `npm run lint` без ошибок
- [ ] `npm run build` без ошибок

### Документация
- [ ] Обновлен `INCOMPLETE_PARTS.md` (если есть технический долг)
- [ ] Обновлены `doc_project/*.md` (новые паттерны/исправления)
- [ ] Добавлен комментарий для новых компонентов

### Релиз
- [ ] Версия `INCOMPLETE_PARTS.md` обновлена (x.y.z → x.y.+1)
- [ ] Краткое описание изменений готово для Git commit

---

**Версия**: 1.0  
**Дата создания**: 2026-05-04  
**Автор**: Kilo
