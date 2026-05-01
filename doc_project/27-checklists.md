# ✅ Чек-листы

## 📋 Содержание

- [Чек-лист для запуска полного цикла](#чек-лист-для-запуска-полного-цикла)
- [Чек-лист для добавления нового маппера](#чек-лист-для-добавления-нового-маппера)
- [Чек-лист для деплоя](#чек-лист-для-деплоя)

---

## Чек-лист для запуска полного цикла

### При準備

- [ ] Docker Desktop запущен
- [ ] PostgreSQL контейнеры (`kilo-import-pg-service`, `kilo-import-pg-visary`) статус **Running**
- [ ] В `KiloImportService.Web/.env.local` задан токен Visary:
  ```env
  VITE_VISARY_API_URL=https://isup-alfa-test.k8s.npc.ba
  VITE_VISARY_API_TOKEN=<JWT_без_префикса>
  ```

### 1. Запуск Backend

- [ ] Открой консоль в папке `KiloImportService.Api`
- [ ] Выполни `dotnet build`
- [ ] Выполни `dotnet run`
- [ ] В логах должен быть:
  ```
  [INF] Now listening on: http://localhost:5000
  ```
- [ ] Проверь через curl: `curl http://localhost:5000/health` → `{"status":"Healthy"}`
- [ ] Проверь через Swagger: http://localhost:5000/swagger

### 2. Запуск Frontend

#### Вариант A: Docker Desktop UI

- [ ] В Docker Desktop → Containers → `kilo-import-frontend` → **Start**
- [ ] Проверь порты: должен быть `5173`

#### Вариант B: Локально

- [ ] Открой консоль в папке `KiloImportService.Web`
- [ ] Выполни `npm install` (если не был)
- [ ] Выполни `npm run dev`
- [ ] В логах должен быть:
  ```
  VITE v5.x.x ready in xxx ms
  ➜ Local: http://localhost:5173
  ```

### 3. Тестирование

- [ ] Открой http://localhost:5173 в браузере
- [ ] Выбери тип импорта: "Финмодель"
- [ ] Выбери проект из списка (первая опция → `selectedProjectState`)
- [ ] Выбери объект строительства (должен появиться после выбора проекта)
- [ ] Загрузи Excel файл с колонкой "Тип отделки"
  - Пример значения: "Черновая"
- [ ] Нажми "Запустить импорт"
- [ ] Проверь логи Backend → должен быть этап валидации

### 4. Проверка результата

- [ ] Подключись к БД Visary:
  ```bash
  docker exec -it kilo-import-pg-visary psql -U visary -d visary_webapi_db
  ```
- [ ] Выполни запрос:
  ```sql
  SELECT "Id", "Title", "FinishingMaterialId" 
  FROM "Data"."ConstructionSite" 
  WHERE "Id" = <твой_siteId>;
  ```
- [ ] Проверь поле `FinishingMaterialId`:
  - `3` → "Черновaya"
  - `2` → "Предчистовая"
  - `1` → "Чистовая"

---

## Чек-лист для добавления нового маппера

### Backend

- [ ] Создан класс маппера, реализующий `IImportMapper<T>`
  - Путь: `KiloImportService.Api/Domain/Mapping/YourImportMapper.cs`
- [ ] Добавлены необходимые поля в сущности БД (если нужно)
  - Путь: `KiloImportService.Api/Data/Visary/Entities/`
- [ ] Маппер зарегистрирован в DI (`Program.cs`):
  ```csharp
  builder.Services.AddScoped<IImportMapper<YourImportType>, YourImportMapper>();
  ```
- [ ] Тип импорта добавлен в `ImportTypesController`:
  ```csharp
  new ImportType("yourtypecode", "Название типа")
  ```
- [ ] Создан клиент для внешнего API (если нужно)
  - Путь: `KiloImportService.Api/Domain/Visary/`

### Тесты

- [ ] Создан класс тестов с `IDisposable`
  - Путь: `KiloImportService.Api.Tests/Mapping/YourImportMapperTests.cs`
- [ ] Настроена in-memory БД с уникальным именем:
  ```csharp
  var options = new DbContextOptionsBuilder<VisaryDbContext>()
      .UseInMemoryDatabase($"YourTest_{Guid.NewGuid()}")
      .Options;
  ```
- [ ] Добавлены тестовые данные в БД
- [ ] Тесты покрывают валидацию (успех и ошибки)
- [ ] Тесты покрывают применение изменений
- [ ] Все тесты проходят:
  ```bash
  cd KiloImportService.Api.Tests
  dotnet test --filter "FullyQualifiedName~YourImportMapperTests"
  ```

### Frontend

- [ ] Создан компонент выбора файла (`FileUpload.tsx`)
  - Путь: `KiloImportService.Web/src/components/FileUpload/`
- [ ] Создан компонент выбора типа (`ImportTypePicker.tsx`)
  - Путь: `KiloImportService.Web/src/components/ImportTypePicker/`
- [ ] Добавлен тип импорта в реестр
  - Путь: `KiloImportService.Web/src/services/importsService.ts`
- [ ] Создан хук для загрузки сессии (`useImportSession.ts`)
  - Путь: `KiloImportService.Web/src/hooks/`
- [ ] Добавлен маппинг DTO ↔ UI типы
  - Путь: `KiloImportService.Web/src/types/`

### Инфраструктура

- [ ] PostgreSQL контейнеры запущены
- [ ] Backend подключается к БД (логи при `dotnet run`)
- [ ] Токен Visary актуален (см. `28-faq.md`)
- [ ] Frontend видит новый тип импорта
- [ ] Проверен полный цикл: выбор → загрузка → валидация → применение

---

## Чек-лист для деплоя

### Подготовка

- [ ] Docker Desktop запущен
- [ ] Резервная копия БД создана:
  ```bash
  docker exec kilo-import-pg-service pg_dump -U import_service -d import_service_db > backup.sql
  docker exec kilo-import-pg-visary pg_dump -U visary -d visary_webapi_db > visary_backup.sql
  ```
- [ ] Обновлён `docker-compose.yml` (если нужно)
- [ ] Обновлён `appsettings.json` с прод-конфигурацией
- [ ] Добавлен `VITE_VISARY_API_TOKEN` в Docker-секреты

### Сборка

- [ ] Backend собран:
  ```bash
  cd KiloImportService.Api
  dotnet publish -c Release -o ../bin
  ```
- [ ] Frontend собран:
  ```bash
  cd KiloImportService.Web
  npm run build
  ```
- [ ] Docker image собран:
  ```bash
  docker compose build
  ```

### Деплой

- [ ] Остановлены старые контейнеры:
  ```bash
  docker compose down
  ```
- [ ] Старые контейнеры удалены (если нужно):
  ```bash
  docker system prune -a
  ```
- [ ] Запущены новые контейнеры:
  ```bash
  docker compose up -d
  ```

### Проверка

- [ ] All контейнеры статус **Running**:
  ```bash
  docker compose ps
  ```
- [ ] Backend health endpoint отвечает:
  ```bash
  curl http://localhost:5000/health
  ```
- [ ] Swagger доступен:
  ```
  http://localhost:5000/swagger
  ```
- [ ] Frontend доступен:
  ```
  http://localhost:5173
  ```
- [ ] PostgreSQL порты открыты:
  ```bash
  netstat -ano | findstr :5433
  netstat -ano | findstr :5434
  ```

### Тестирование

- [ ] Backend логи не содержат ошибок:
  ```bash
  docker compose logs backend
  ```
- [ ] Frontend логи не содержат ошибок:
  ```bash
  docker compose logs frontend
  ```
- [ ] Swagger содержит все эндпоинты
- [ ] UI открывается без ошибок в браузере
- [ ] Проверен полный цикл импорта (создай тестовый файл)

---

**Версия**: 1.0  
**Дата**: 2026-05-01  
**Автор**: Kilo
