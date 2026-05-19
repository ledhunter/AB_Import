# 🛠️ Решение проблем

## 📋 Содержание

- [502 Bad Gateway от Vite proxy на `/api/*`](#502-bad-gateway-от-vite-proxy-на-api)
- [Запуск backend при проблемах с Docker CLI](#запуск-backend-при-проблемах-с-docker-cli)
- [Проверка подключения к БД](#проверка-подключения-к-бд)
- [Отладка импорта "Финмодель"](#отладка-импорта-финмодель)
- [Частые ошибки](#частые-ошибки)

---

## 502 Bad Gateway от Vite proxy на `/api/*`

### Симптом

В DevTools браузера:
```
POST http://localhost:5173/api/projects/sync   502 (Bad Gateway)
GET  http://localhost:5173/api/projects/search  502 (Bad Gateway)
```

⚠️ **Не путать с 401**: 401 — токен Visary невалиден (см. [54](./54-visary-token-hot-reload.md)).
**502** означает, что Vite proxy не дозвонился до upstream backend на `localhost:5000`
(или dotnet вернул ошибку соединения).

### Диагностика

```powershell
# 1. Backend контейнер жив?
docker ps --filter "name=kilo-import-backend"
# ожидание: Up X minutes (healthy)

# 2. Прямой вызов backend (мимо Vite):
curl -i http://localhost:5000/health
# ожидание: 200; если "Failed to connect" — backend упал.

# 3. Docker daemon жив?
docker info
# ошибка "failed to connect to the docker API at npipe://..." → Docker Desktop умер.
```

### Корневая причина (наблюдалась 2026-05-19)

Docker Desktop был остановлен (закрыт пользователем / OOM / Windows-засып) → backend
контейнер не работает → Vite proxy на `:5173` форвардит запросы на `:5000`, получает
`ECONNREFUSED` и возвращает клиенту **502 Bad Gateway**.

### Решение

1. Запустить Docker Desktop вручную (`%ProgramFiles%\Docker\Docker\Docker Desktop.exe`).
2. Дождаться, пока `docker info` отработает без ошибки (обычно ~30–90 секунд).
3. Поднять контейнеры из **inner** клона:
   ```powershell
   Set-Location c:\Users\vkgsk\Desktop\AlfaProjects\import\AB_Import\AB_Import
   docker compose --env-file ..\.env up -d backend
   ```
4. Обновить страницу в браузере — Vite proxy дозвонится, 502 уйдёт.

Vite **не нужно** перезапускать: его токен и конфиг proxy остаются актуальными.

---

## Запуск backend при проблемах с Docker CLI

### Проблема: `failed to connect to the docker API`

**Симптом**:
```
failed to connect to the docker API at npipe:////./pipe/dockerDesktopLinuxEngine
```

**Решение**: Используй Docker Desktop UI вместо CLI.

### Шаг 1: Запусти PostgreSQL контейнеры через UI

1. Открой Docker Desktop
2. Перейди в раздел **Containers**
3. Найди:
   - `kilo-import-pg-service` — служебная БД
   - `kilo-import-pg-visary` — целевая БД Visary
4. Нажми **Start** для каждого
5. Проверь статус — должен быть **Running**

### Шаг 2: Проверь порты

В Docker Desktop → Containers → кликни на контейнер → вкладка **Ports**:

- `5433` → PostgreSQL service-db
- `5434` → PostgreSQL visary-db

### Шаг 3: Запусти backend

```bash
cd "C:\Users\ancye\Downloads\vs code\Alfa\KiloImportService.Api"
dotnet build
dotnet run
```

**Ожидаемый вывод**:
```
[HH:mm:ss INF] Starting KiloImportService.Api...
[HH:mm:ss INF] Applying ImportServiceDb migrations...
[HH:mm:ss INF] Now listening on: http://localhost:5000
```

---

## Проверка подключения к БД

### 1. Проверь логи Backend

При запуске `dotnet run` в консоли должно быть:

```
[HH:mm:ss INF] Connection string: Host=localhost;Port=5433;Database=import_service_db...
[HH:mm:ss INF] Applying ImportServiceDb migrations...
[HH:mm:ss INF] Migrations applied successfully.
```

**Если ошибка подключения**:
```
[Npgsql.EntityFrameworkCore.PostgreSQL] Connection refused
```

**Решение**: Убедись, что контейнеры PostgreSQL запущены через Docker Desktop UI.

### 2. Проверь через curl

В новой консоли:

```bash
curl http://localhost:5000/health
```

**Ожидаемый ответ**:
```json
{"status":"Healthy","services":[]}
```

**Если 502 Bad Gateway**:
- Backend не запущен 或 не отвечает
- Проверь логи `dotnet run`

### 3. Проверь через Swagger

Открой в браузере:
```
http://localhost:5000/swagger
```

**Должен отобразиться API документ** с эндпоинтами:
- `/api/imports`
- `/api/projects/search`
- `/api/importtypes`

**Если 404 Not Found**:
- Backend не отдает Swagger
- Проверь `Program.cs`: `app.UseSwagger()` и `app.UseSwaggerUI()`

---

## Отладка импорта "Финмодель"

### Шаг 1: Запусти полный цикл

#### Frontend (Docker Desktop UI)

1. В Docker Desktop → Containers → найди `kilo-import-frontend`
2. Нажми **Start**

**Или локально**:
```bash
cd "C:\Users\ancye\Downloads\vs code\Alfa\KiloImportService.Web"
npm run dev
```

#### Backend (команда)

```bash
cd "C:\Users\ancye\Downloads\vs code\Alfa\KiloImportService.Api"
dotnet run
```

### Шаг 2: Загрузи тестовый файл

1. Открой http://localhost:5173
2. Выбери тип импорта: **"Финмодель"**
3. Выбери проект из списка
4. Выбери объект строительства
5. Загрузи Excel файл с колонкой "Тип отделки"
6. Нажми **"Запустить импорт"**

### Шаг 3: Проверь логи Backend

В консоли `dotnet run` должно быть:

```
[HH:mm:ss INF] Session {sessionId}: starting VALIDATE stage
[HH:mm:ss INF] Session {sessionId}: calling mapper.ValidateAsync
FinModelImportMapper.ValidateAsync: siteId={siteId}, rows={count}
FinModelImportMapper.ValidateAsync: querying ConstructionSite {siteId}
FinModelImportMapper.ValidateAsync: ConstructionSite query completed siteFound=True
FinModelImportMapper.ValidateAsync: processing row 1/1
FinModelImportMapper.ValidateAsync: completed mappedRows=1 fileErrors=0
[HH:mm:ss INF] Session {sessionId}: mapper.ValidateAsync returned rows=1 errors=0
```

**Если нет логов**:
- Backend не запущен
- Error в `dotnet run` консоли

**Если ошибка валидации**:
- Проверь колонку в Excel: "Тип отделки"
- Проверь значение: "Черновая" / "Предчистовая" / "Чистовая"

### Шаг 4: Проверь в БД

Подключись к PostgreSQL:

```bash
docker exec -it kilo-import-pg-visary psql -U visary -d visary_webapi_db
```

Выполни запрос:

```sql
SELECT "Id", "Title", "FinishingMaterialId" 
FROM "Data"."ConstructionSite" 
WHERE "Id" = <твой_siteId>;
```

**Ожидаемый результат**: `FinishingMaterialId` обновился на 3 (Черновая), 2 (Предчистовая) или 1 (Чистовая).

---

## Частые ошибки

### Ошибка 1: Frontend не запускается

**Симптом**: `connectionfailure` на http://localhost:5173

**Причина**: 
- Docker Desktop не запущен (для Docker-режима)
- Нет установки Node.js/dependencies (для локального запуска)

**Решение**: См. `../FIX-FRONTEND.md`

### Ошибка 2: Backend не отвечает на localhost:5000

**Симптом**: `curl http://localhost:5000/health` → Connection refused

**Причина**:
- PostgreSQL контейнеры не запущены
- Backend упал при старте из-за ошибки подключения к БД

**Решение**:
1. Запусти PostgreSQL через Docker Desktop UI
2. Запусти backend: `dotnet run`
3. Проверь логи в консоли

### Ошибка 3: Select объектов строительства пустой

**Симптом**: После выбора проекта — список объектов пуст

**Причина**: 
- Токен Visary истёк
- Visary API недоступен

**Решение**:
1. Проверь **корневой `.env`** репозитория (SSOT — см. [54](./54-visary-token-hot-reload.md)):
   ```
   Visary__BearerToken=eyJhbGci...
   VITE_VISARY_API_TOKEN=eyJhbGci...
   ```
2. Обнови токен (см. `28-faq.md`)
3. Перезапусти процессы: `docker compose up -d --force-recreate backend frontend` (или Ctrl+C → `npm run dev` локально)

### Ошибка 4: FinModelImportMapper не находит объект

**Симптом**: В логах:
```
FinModelImportMapper.ValidateAsync: ConstructionSite query completed siteFound=False
```

**Причина**:
- Некорректный `siteId`
- Объект скрыт (`Hidden = true`)

**Решение**:
1. Проверь `siteId` в UI
2. Проверь в БД: `SELECT "Id", "Hidden" FROM "Data"."ConstructionSite" WHERE "Id" = ...`
3. Раскрой объект в Visary (сними галочку "Hidden")

### Ошибка 5: Миграция не применяется

**Симптом**: При запуске backend:
```
 Npgsql.PostgresException (0x80004005): 42703: column "FileSha256" does not exist
```

**Причина**: Миграция не применена в БД

**Решение**:

1. Подключись к БД:
   ```bash
   docker exec -it kilo-import-pg-service psql -U import_service -d import_service_db
   ```

2. Проверь статус миграций:
   ```sql
   SELECT * FROM "__EFMigrationsHistory" WHERE "MigrationId" LIKE '%File Sha256%';
   ```

3. Примени миграцию вручную (SQL):
   ```sql
   ALTER TABLE "import"."import_sessions" DROP COLUMN "FileSha256";
   DROP INDEX IF EXISTS "IX_ImportSession_TypeAndSha";
   CREATE INDEX "IX_ImportSession_Type" ON "import"."import_sessions" ("ImportTypeCode");
   INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion") 
   VALUES ('20260430213808_RemoveFileSha256Constraint', '9.0.4');
   ```

4. Выйди: `\q`
5. Перезапусти backend: `dotnet run`

---

## 📌 Важно

- **Docker Desktop UI** предпочтительнее CLI для диагностики
- **Логи Backend** — основной источник информации об ошибках
- **Swagger** — быстрая проверка работоспособности API
- **Порт 5000** — backend, **5173** — frontend
- **Токен Visary** обновляется каждые 24 часа (см. `28-faq.md`)

---

**Версия**: 1.0  
**Дата**: 2026-05-01  
**Автор**: Kilo
