# 🚀 Полный запуск через Docker Desktop UI

**Дата**: 2026-05-01  
**Статус**: Требует Docker Desktop UI

---

## ⚠️ Проблема с Docker CLI

Docker CLI использует контекст `desktop-linux`, который требует WSL2. На этой машине WSL2 не настроен, поэтому CLI не подключается:

```
failed to connect to the docker API at npipe:////./pipe/dockerDesktopLinuxEngine
```

**Решение**: Использовать Docker Desktop UI для управления контейнерами.

---

## ✅ Инструкция по полному запуску

### Шаг 1: Запустить PostgreSQL через Docker Desktop UI

1. **Открой Docker Desktop**
2. Перейди в раздел **Containers**
3. Найди и запусти:
   - `kilo-import-pg-service` (служебная БД на порту 5433)
   - `kilo-import-pg-visary` (_visary БД на порту 5434)
4. Убедись, что статус → **Running** (зелёный)

### Шаг 2: Проверить порты

Для каждого контейнера:
1. Кликни на контейнер
2. Вкладка **Ports**
3. Убедись, что отображаются:
   - `5433/tcp` → `0.0.0.0:5433`
   - `5434/tcp` → `0.0.0.0:5434`

### Шаг 3: Запустить Backend

После запуска PostgreSQL (через ~10 секунд):

```powershell
cd "C:\Users\ancye\Downloads\vs code\Alfa\KiloImportService.Api"
dotnet run
```

**Ожидаемый вывод**:
```
[04:XX:XX INF] Applying ImportServiceDb migrations…
[04:XX:XX INF] Now listening on: http://localhost:5000
```

### Шаг 4: Проверить Backend

В новой консоли:
```powershell
curl http://localhost:5000/health
```

**Ожидаемый ответ**:
```json
{"status":"Healthy","services":[]}
```

### Шаг 5: Проверить UI

Frontend уже запущен на `http://localhost:5173`.

Открой в браузере:
```
http://localhost:5173
```

---

## 🧪 Проверка полного цикла

### 1. Выбор проекта

1. Открой http://localhost:5173
2. В форме импорта найди Select **"Проект"**
3. Кликни на Select → должен появиться список проектов

### 2. Выбор объекта строительства

1. Выбери любой проект
2. В Select **"Объект строительства"** должны появиться объекты

### 3. Импорт "Финмодель"

1. Выбери тип импорта: **"Финмодель"**
2. Загрузи Excel файл с колонкой "Тип отделки" (значение: "Черновая")
3. Нажми **"Запустить импорт"**

### 4. Проверка в БД

```bash
# Подключись к PostgreSQL
docker exec -it kilo-import-pg-visary psql -U visary -d visary_webapi_db

# Выполни запрос
SELECT "Id", "Title", "FinishingMaterialId" 
FROM "Data"."ConstructionSite" 
WHERE "Id" = <твой_siteId>;
```

---

## 📊 Статус сервисов после запуска

| Сервис | Порт | Статус | Способ запуска |
|--------|------|--------|----------------|
| PostgreSQL (service-db) | 5433 | ⏳ Ожидает | Docker Desktop UI |
| PostgreSQL (visary-db) | 5434 | ⏳ Ожидает | Docker Desktop UI |
| Backend API | 5000 | ⏳ Ожидает | `dotnet run` |
| Frontend UI | 5173 | ✅ Запущен | `npm run dev` |

---

## 🔍 Если контейнеры не найдены

Если `kilo-import-pg-*` контейнеры не существуют:

### Вариант A: Создать через docker-compose

```bash
cd "C:\Users\ancye\Downloads\vs code\Alfa"
docker-compose up -d pg-service pg-visary
```

### Вариант B: Создать через Docker Desktop UI

1. Docker Desktop → **Images** → найди `postgres:16-alpine`
2. Кликни **Run** для каждого контейнера
3. Настрой:
   - Контейнер `kilo-import-pg-service`:
     - Port: `5433:5433`
     - Env: `POSTGRES_DB=import_service_db`, `POSTGRES_USER=import_service`, `POSTGRES_PASSWORD=import_service_pwd`
   - Контейнер `kilo-import-pg-visary`:
     - Port: `5434:5434`
     - Env: `POSTGRES_DB=visary_webapi_db`, `POSTGRES_USER=visary`, `POSTGRES_PASSWORD=visary_pwd`

---

## ⚠️ Частые проблемы

### Проблема 1: Backend не может подключиться к БД

**Симптом**: В логах `dotnet run`:
```
Npgsql.PostgresException (0x80004005): Connection refused
```

**Решение**:
1. Проверь статус контейнера `kilo-import-pg-service` в Docker Desktop UI
2. Убедись, что статус **Running**
3. Подожди ~10 секунд после Start
4. Попробуй снова: `dotnet run`

### Проблема 2: Токен истёк

**Симптом**: 401 Unauthorized в логах backend

**Решение**: Обнови токен в `KiloImportService.Web/.env.local`

---

**Версия**: 1.0  
**Дата**: 2026-05-01  
**Автор**: Kilo
