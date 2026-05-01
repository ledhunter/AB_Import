# ❓ Частые вопросы

## 📋 Содержание

- [Почему Select с проектами пустой?](#почему-select-с-проектами-пустой)
- [Почему Select с объектами строительства пустой?](#почему-select-с-объектами-строительства-пустой)
- [Backend не отвечает на localhost:5000](#backend-не-отвечает-на-localhost5000)
- [Как обновить токен Visary?](#как-обновить-токен-visary)
- [Почему FinModel импорт выдаёт ошибку "site not found"?](#почему-finmodel-импорт-выдаёт-ошибку-site-not-found)
- [Frontend не запускается: connectionfailure](#frontend-не-запускается-connectionfailure)
- [Миграция не применяется](#миграция-не-применяется)

---

## Почему Select с проектами пустой?

### Причина 1: Backend не запущен

**Симптом**: DevTools → Network → `/api/projects/search` → 502 Bad Gateway

**Решение**:
1. Запусти backend:
   ```bash
   cd "C:\Users\ancye\Downloads\vs code\Alfa\KiloImportService.Api"
   dotnet run
   ```
2. Проверь логи:
   ```
   [INF] Now listening on: http://localhost:5000
   ```
3. Проверь health endpoint:
   ```bash
   curl http://localhost:5000/health
   ```

### Причина 2: Токен Visary истёк

**Симптом**: Backend логи → `401 Unauthorized`

**Решение**: Обнови токен (см. [Как обновить токен Visary?](#как-обновить-токен-visary))

### Причина 3: Visary API недоступен

**Симптом**: Backend логи → `Visary API error: 503 Service Unavailable`

**Решение**: Проверь доступность Visary API:
```bash
curl -H "Authorization: Bearer <твой_токен>" https://isup-alfa-test.k8s.npc.ba/api/visary/listview/constructionproject
```

---

## Почему Select с объектами строительства пустой?

### Причина 1: Не выбран проект

**Симптом**: Select объектов disabled

**Решение**: Сначала выбери проект в первом Select

### Причина 2: У объектов `Hidden = true`

**Симптом**: Backend логи → `ConstructionSite query completed siteFound=False`

**Решение**:
1. Подключись к БД:
   ```bash
   docker exec -it kilo-import-pg-visary psql -U visary -d visary_webapi_db
   ```
2. Раскрой объекты:
   ```sql
   UPDATE "Data"."ConstructionSite" SET "Hidden" = false WHERE "Id" IN (<список_id>);
   ```
3. Выйди: `\q`

### Причина 3: Токен Visary истёк

**Симптом**: DevTools → Network → `/api/visary/listview/constructionsite/onetomany/Project` → 401

**Решение**: Обнови токен (см. [Как обновить токен Visary?](#как-обновить-токен-visary))

---

## Backend не отвечает на localhost:5000

### Причина 1: Backend не запущен

**Решение**:
```bash
cd "C:\Users\ancye\Downloads\vs code\Alfa\KiloImportService.Api"
dotnet run
```

### Причина 2: PostgreSQL не запущен

**Симптом**: В логах `dotnet run`:
```
Npgsql.PostgresException (0x80004005): Connection refused
```

**Решение**:
1. Открой Docker Desktop UI
2. Найди `kilo-import-pg-service`
3. Нажми **Start**
4. Запусти backend снова

### Причина 3: Порт занят другим процессом

**Симптом**: В логах `dotnet run`:
```
System.IO.IOException: Failed to bind to address http://localhost:5000
```

**Решение**:
1. Освободи порт 5000:
   ```bash
   netstat -ano | findstr :5000
   taskkill /PID <pid> /F
   ```
2. Или запусти на другом порту:
   ```bash
   dotnet run --urls "http://localhost:5001"
   ```

---

## Как обновить токен Visary?

### Шаг 1: Получи новый токен

Обратись к администратору Visary илиfetch токен из браузера:

1. Открой Visary в браузере
2. Войди под своим аккаунтом
3. Открой DevTools → Application → Local Storage → найди токен
4. Скопируй токен (после `jwt=`)

### Шаг 2: Обнови `.env.local`

В папке `KiloImportService.Web` создай/обнови `.env.local`:

```env
VITE_VISARY_API_URL=https://isup-alfa-test.k8s.npc.ba
VITE_VISARY_API_TOKEN=<НОВЫЙ_ТОКЕН_БЕЗ_ПРЕФИКСА_Bearer>
```

⚠️ **Важно**: Токен **без** префикса `"Bearer "`

### Шаг 3: Перезапусти dev-сервер

```bash
cd "C:\Users\ancye\Downloads\vs code\Alfa\KiloImportService.Web"
npm run dev
```

### Примечание

Токен обычно действует 24 часа. После истечения API будет возвращать `401 Unauthorized`.

---

## Почему FinModel импорт выдаёт ошибку "site not found"?

### Причина 1: Некорректный siteId

**Симптом**: В логах Backend:
```
FinModelImportMapper.ValidateAsync: ConstructionSite query completed siteFound=False
```

**Решение**:
1. Проверь `siteId` в UI (должен совпадать с тем, что в БД)
2. Проверь в БД:
   ```sql
   SELECT "Id", "Title", "Hidden" 
   FROM "Data"."ConstructionSite" 
   WHERE "Id" = <твой_siteId>;
   ```

### Причина 2: Объект скрыт

**Симптом**: В БД `Hidden = true`

**Решение**:
```sql
UPDATE "Data"."ConstructionSite" SET "Hidden" = false WHERE "Id" = <твой_siteId>;
```

### Причина 3: siteId не передан

**Симптом**: В логах Backend:
```
FinModelImportMapper.ValidateAsync: siteId=, rows=1
```

**Решение**: Выбери объект строительства в UI перед загрузкой файла

---

## Frontend не запускается: connectionfailure

### Причина 1: Docker Desktop не запущен

**Решение**: Запусти Docker Desktop и дождись статуса **Running**

### Причина 2: Нет Node.js/dependencies

**Решение**:
```bash
cd "C:\Users\ancye\Downloads\vs code\Alfa\KiloImportService.Web"
npm install
npm run dev
```

**См. также**: `FIX-FRONTEND.md`

---

## Миграция не применяется

### Причина 1: EF Core tools не работают в Docker

**Решение**: Примени миграцию вручную через SQL.

**Пример** (для миграции `RemoveFileSha256Constraint`):

1. Подключись к БД:
   ```bash
   docker exec -it kilo-import-pg-service psql -U import_service -d import_service_db
   ```

2. Выполни SQL:
   ```sql
   ALTER TABLE "import"."import_sessions" DROP COLUMN "FileSha256";
   DROP INDEX IF EXISTS "IX_ImportSession_TypeAndSha";
   CREATE INDEX "IX_ImportSession_Type" ON "import"."import_sessions" ("ImportTypeCode");
   INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion") 
   VALUES ('20260430213808_RemoveFileSha256Constraint', '9.0.4');
   ```

3. Выйди: `\q`

### Причина 2: Конфликт миграций

**Решение**:
1. Проверь статус:
   ```sql
   SELECT * FROM "__EFMigrationsHistory" ORDER BY "MigrationId";
   ```
2. Удали противоречащую миграцию из таблицы:
   ```sql
   DELETE FROM "__EFMigrationsHistory" WHERE "MigrationId" = '<имя_миграции>';
   ```
3. Создай новую миграцию:
   ```bash
   cd KiloImportService.Api
   dotnet ef migrations add <имя_миграции>
   ```

---

## 📌 Важно

- **Docker Desktop UI** предпочтительнее CLI для диагностики
- **Логи Backend** — основной источник информации об ошибках
- **Swagger** — быстрая проверка работоспособности API
- **Токен Visary** обновляется каждые 24 часа
- **Порт 5000** — backend, **5173** — frontend

---

**Версия**: 1.0  
**Дата**: 2026-05-01  
**Автор**: Kilo
