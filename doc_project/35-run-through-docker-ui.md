# 🎯 Запуск полного цикла через Docker Desktop UI

**Дата**: 2026-05-01  
**Статус**: Готово к запуску

---

## ⚠️ Ограничение

Docker CLI не подключается к daemon из-за конфигурации `desktop-linux` контекста (требует WSL2).

**Решение**: Управление контейнерами через **Docker Desktop UI**.

---

## 🚀 Пошаговая инструкция

### Шаг 1: Запустить PostgreSQL контейнеры

1. **Открой Docker Desktop**
   - Иконка Docker в трее Windows → кликни
   - Или найди "Docker Desktop" в меню Пуск

2. **Дождись статуса Running**
   - Внизу интерфейса Docker Desktop должен показать **"Docker Desktop is running"**

3. **Запусти PostgreSQL контейнеры**
   - Перейди во вкладку **Containers**
   - Найди контейнеры:
     - `kilo-import-pg-service`
     - `kilo-import-pg-visary`
   - Нажми кнопку **Start** для каждого
   - Подожди ~10 секунд
   - Убедись, что статус стал **Running** (зелёный значок)

### Шаг 2: Проверь порты

Для каждого контейнера:
1. Кликни на контейнер
2. Перейди во вкладку **Ports**
3. Убедись, что отображаются:
   - `5433/tcp` → `0.0.0.0:5433` (service-db)
   - `5434/tcp` → `0.0.0.0:5434` (visary-db)

### Шаг 3: Запусти Backend

После запуска PostgreSQL:

```powershell
cd "C:\Users\ancye\Downloads\vs code\Alfa\KiloImportService.Api"
dotnet run
```

**Ожидаемый вывод**:
```
[04:XX:XX INF] Starting KiloImportService.Api...
[04:XX:XX INF] Applying ImportServiceDb migrations...
[04:XX:XX INF] Now listening on: http://localhost:5000
```

### Шаг 4: Проверь Backend

В новой консоли PowerShell:

```powershell
curl http://localhost:5000/health
```

**Ожидаемый ответ**:
```json
{"status":"Healthy","services":[]}
```

### Шаг 5: Проверь Frontend

Frontend уже запущен на `http://localhost:5173`.

Открой в браузере:
```
http://localhost:5173
```

---

## 🧪 Smoke-тест

### 1. Проверка загрузки проектов

1. Открой http://localhost:5173 в браузере
2. В форме импорта найди Select **"Проект"**
3. Кликни на Select
4. Проверь DevTools → Network:
   - Запрос: `GET /api/projects/search?q=&limit=50`
   - Ответ: `200 OK` с массивом проектов

### 2. Проверка загрузки объектов строительства

1. Выбери любой проект
2. В Select **"Объект строительства"** должны появиться объекты
3. Проверь DevTools → Network:
   - Запрос: `POST /api/visary/listview/constructionsite/onetomany/Project`
   - Ответ: `200 OK`

### 3. Проверка импорта "Финмодель"

1. Выбери тип импорта: **"Финмодель"**
2. Загрузи Excel файл с колонкой "Тип отделки" (значение: "Черновая")
3. Нажми **"Запустить импорт"**
4. Проверь логи Backend в консоли `dotnet run`

### 4. Проверка в БД

```powershell
# Подключись к PostgreSQL через Docker Desktop UI
docker exec -it kilo-import-pg-visary psql -U visary -d visary_webapi_db

# Выполни запрос
SELECT "Id", "Title", "FinishingMaterialId" 
FROM "Data"."ConstructionSite" 
WHERE "Id" = <твой_siteId>;

# Выход из psql
\q
```

---

## 🔍 Диагностика проблем

### Проблема: Backend не отвечает

**Симптом**: `curl http://localhost:5000/health` → Connection refused

**Проверка**:
1. В Docker Desktop UI → Containers → `kilo-import-pg-service` → статус **Running**?
2. Подождал ~10 секунд после Start PostgreSQL?
3. Проверь логи Backend: `dotnet run` → есть ли ошибки?

### Проблема: Select пустой

**Симптом**: В UI Select "Проект" не показывает опции

**Проверка**:
1. Проверь DevTools → Network → `/api/projects/search`
2. Проверь токен в `.env.local`
3. Проверь логи Backend на ошибки 401/403

### Проблема: Токен истёк

**Симптом**: 401 Unauthorized в логах backend

**Решение**:
1. Обнови токен в `KiloImportService.Web/.env.local`
2. Перезапусти frontend: `npm run dev`

---

## 📊 Статус сервисов

| Сервис | Порт | Статус | Способ запуска |
|--------|------|--------|----------------|
| PostgreSQL (service-db) | 5433 | ⏳ Docker Desktop UI | Запустить через UI |
| PostgreSQL (visary-db) | 5434 | ⏳ Docker Desktop UI | Запустить через UI |
| Backend API | 5000 | ⏳ `dotnet run` | Запустить в консоли |
| Frontend UI | 5173 | ✅ `npm run dev` | Уже запущен |

---

## 📝 Созданная документация

- `doc_project/34-full-run-instructions.md` — полная инструкция по запуску
- `doc_project/33-docker-cli-troubleshooting.md` — объяснение проблемы с Docker CLI
- `doc_project/32-smoke-test-full.md` — сценарий smoke-теста

---

## 💡 Важное замечание

Docker CLI не может управлять контейнерами из-за ограничений окружения. **Docker Desktop UI — единственный надёжный способ управлять контейнерами** в этой конфигурации.

После запуска контейнеров через UI:
- Docker CLI будет работать корректно
- Миграции и部署 пройдут успешно
- Полный цикл импорта заработает

---

**Версия**: 1.0  
**Дата**: 2026-05-01  
**Автор**: Kilo
