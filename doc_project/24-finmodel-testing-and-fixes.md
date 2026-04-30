# 🧪 Тестирование и исправление FinModel импорта

## 📋 Описание

Документация по процессу тестирования и исправления ошибок при реализации импорта "Финмодель". Описывает типичные проблемы, возникающие при добавлении новых мапперов, и правильные подходы к их решению.

---

## ✅ Правильная реализация тестов с БД

### Проблема
При создании тестов для `FinModelImportMapper` возникла ошибка `NullReferenceException`, потому что в тестах передавался `null!` вместо реального `VisaryDbContext`.

### Решение: In-Memory БД для тестов

```csharp
using KiloImportService.Api.Data.Visary;
using KiloImportService.Api.Data.Visary.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

public class FinModelImportMapperTests : IDisposable
{
    private readonly FinModelImportMapper _mapper;
    private readonly VisaryDbContext _dbContext;

    public FinModelImportMapperTests()
    {
        _mapper = new FinModelImportMapper(NullLogger<FinModelImportMapper>.Instance);
        
        // ✅ Создаём in-memory БД для каждого теста
        var options = new DbContextOptionsBuilder<VisaryDbContext>()
            .UseInMemoryDatabase($"FinModelTest_{Guid.NewGuid()}")  // 👈 Уникальное имя БД
            .Options;
        _dbContext = new VisaryDbContext(options);
        
        // ✅ Добавляем тестовые данные
        _dbContext.ConstructionSites.Add(new ConstructionSite
        {
            Id = 123,
            Title = "Тестовый объект",
            Hidden = false
        });
        _dbContext.SaveChanges();
    }

    public void Dispose()
    {
        _dbContext?.Dispose();  // 👈 Очистка ресурсов
    }

    [Fact]
    public void ValidateAsync_ValidData_Success()
    {
        // Arrange
        var row = new ParsedRow(
            SourceRowNumber: 2,
            Sheet: "inputs",
            Cells: new Dictionary<string, string> { ["Тип отделки"] = "Черновая" }
        );

        // Act
        var result = _mapper.ValidateAsync(
            new ImportContext(Guid.NewGuid(), null, 123, null),
            new[] { row },
            _dbContext,  // ✅ Передаём реальный контекст
            CancellationToken.None
        ).Result;

        // Assert
        Assert.Single(result.Rows);
        Assert.True(result.Rows[0].IsValid);
    }
}
```

### ⚠️ Важно

- **Уникальное имя БД**: Используйте `Guid.NewGuid()` для каждого теста, чтобы избежать конфликтов
- **IDisposable**: Всегда реализуйте `IDisposable` для очистки контекста
- **Тестовые данные**: Добавляйте минимально необходимые данные в БД
- **SaveChanges**: Не забывайте вызвать `SaveChanges()` после добавления данных

---

## ❌ Типичные ошибки

### Ошибка 1: Передача null вместо DbContext

```csharp
// ❌ НЕПРАВИЛЬНО - приведёт к NullReferenceException
var result = _mapper.ValidateAsync(
    new ImportContext(Guid.NewGuid(), null, 123, null),
    new[] { row },
    null!,  // ❌ Маппер попытается обратиться к БД!
    CancellationToken.None
).Result;
```

**Почему ломается**: Маппер выполняет запрос к БД для проверки существования объекта:
```csharp
var site = await visaryDb.ConstructionSites  // ❌ visaryDb == null!
    .AsNoTracking()
    .FirstOrDefaultAsync(s => s.Id == context.VisarySiteId.Value && !s.Hidden, ct);
```

### Ошибка 2: Общая БД для всех тестов

```csharp
// ❌ НЕПРАВИЛЬНО - тесты будут влиять друг на друга
var options = new DbContextOptionsBuilder<VisaryDbContext>()
    .UseInMemoryDatabase("SharedTestDb")  // ❌ Одно и то же имя!
    .Options;
```

**Проблема**: Тесты могут выполняться параллельно, и данные одного теста повлияют на другой.

### Ошибка 3: Забыли SaveChanges

```csharp
// ❌ НЕПРАВИЛЬНО - данные не сохранены в БД
_dbContext.ConstructionSites.Add(new ConstructionSite { Id = 123 });
// ❌ Нет SaveChanges()!

var result = _mapper.ValidateAsync(...).Result;
// ❌ Маппер не найдёт объект в БД
```

---

## 🔧 Backend и PostgreSQL в Docker

### Проблема: Backend не отвечает на localhost:5000

**Симптомы**:
- Frontend получает `502 Bad Gateway` на `/api/projects/search`
- `curl http://localhost:5000/health` не отвечает
- `netstat -ano | findstr :5000` показывает пустой результат

### Причина
PostgreSQL контейнеры не запущены, backend не может подключиться к БД и падает при старте.

### ✅ Решение

1. **Проверьте статус контейнеров** в Docker Desktop UI
2. **Запустите PostgreSQL контейнеры**:
   ```powershell
   docker start kilo-import-pg-service kilo-import-pg-visary
   ```
3. **Дождитесь готовности БД** (healthcheck в docker-compose.yml)
4. **Запустите backend**:
   ```powershell
   cd KiloImportService.Api
   dotnet run
   ```

### Конфигурация для локального запуска

`appsettings.json`:
```json
{
  "ConnectionStrings": {
    "ServiceDb": "Host=localhost;Port=5433;Database=import_service_db;Username=import_service;Password=import_service_pwd",
    "VisaryDb": "Host=localhost;Port=5434;Database=visary_webapi_db;Username=visary;Password=visary_pwd"
  },
  "Visary": {
    "BaseUrl": "https://isup-alfa-test.k8s.npc.ba",
    "BearerToken": "eyJhbGci..."  // 👈 Обновляйте токен!
  }
}
```

**Порты**:
- `5433` → PostgreSQL service-db (в Docker)
- `5434` → PostgreSQL visary-db (в Docker)
- `5000` → Backend API (локально)
- `5173` → Frontend (локально)

---

## 🎯 Проблема: Docker CLI не подключается к Docker Desktop

### Симптомы
```
failed to connect to the docker API at npipe:////./pipe/dockerDesktopLinuxEngine
```

### Причины
1. Docker Desktop запущен, но CLI не может подключиться
2. Проблема с правами доступа
3. Docker Desktop ещё не полностью инициализирован

### ✅ Решение
Используйте **Docker Desktop UI** вместо CLI:
1. Откройте Docker Desktop
2. Перейдите в раздел "Containers"
3. Найдите нужные контейнеры
4. Используйте кнопки Start/Stop/Restart

---

## 📍 Применение в проекте

| Компонент | Файл | Назначение |
|-----------|------|------------|
| FinModelImportMapper | `Domain/Mapping/FinModelImportMapper.cs` | Маппер для импорта финмодели |
| FinModelImportMapperTests | `Tests/Mapping/FinModelImportMapperTests.cs` | Unit-тесты с in-memory БД |
| ConstructionSite | `Data/Visary/Entities/ConstructionSite.cs` | Сущность с полем FinishingMaterialId |
| appsettings.json | `KiloImportService.Api/appsettings.json` | Конфигурация БД и Visary API |

---

## 🎯 Чек-лист при добавлении нового маппера

### Backend
- [ ] Создан класс маппера, реализующий `IImportMapper`
- [ ] Добавлены необходимые поля в сущности БД
- [ ] Маппер зарегистрирован в DI (`Program.cs`)
- [ ] Тип импорта добавлен в `ImportTypesController`
- [ ] Создан клиент для внешнего API (если нужно)

### Тесты
- [ ] Создан класс тестов с `IDisposable`
- [ ] Настроена in-memory БД с уникальным именем
- [ ] Добавлены тестовые данные в БД
- [ ] Тесты покрывают валидацию (успех и ошибки)
- [ ] Тесты покрывают применение изменений
- [ ] Все тесты проходят: `dotnet test`

### Инфраструктура
- [ ] PostgreSQL контейнеры запущены
- [ ] Backend подключается к БД
- [ ] Токен Visary актуален
- [ ] Frontend видит новый тип импорта
- [ ] Проверен полный цикл: выбор → загрузка → валидация → применение

---

## 🐛 Отладка

### Проверка backend
```powershell
# Проверить, слушает ли backend порт 5000
netstat -ano | findstr :5000

# Проверить health endpoint
curl http://localhost:5000/health

# Посмотреть логи
dotnet run  # в консоли будут логи
```

### Проверка PostgreSQL
```powershell
# Проверить порты
netstat -ano | findstr :5433
netstat -ano | findstr :5434

# Подключиться к БД
docker exec -it kilo-import-pg-service psql -U import_service -d import_service_db
```

### Проверка тестов
```powershell
# Запустить все тесты
dotnet test

# Запустить только тесты маппера
dotnet test --filter "FullyQualifiedName~FinModelImportMapperTests"

# Подробный вывод
dotnet test --logger "console;verbosity=detailed"
```

---

## 📚 См. также

- [23-finmodel-import.md](./23-finmodel-import.md) - Полная документация импорта "Финмодель"
- [17-backend-tests-xunit.md](./17-backend-tests-xunit.md) - Паттерны тестирования backend
- [12-ef-core-migrations.md](./12-ef-core-migrations.md) - Работа с EF Core миграциями

---

**Версия**: 1.0  
**Дата**: 2026-04-30  
**Автор**: Cascade AI
