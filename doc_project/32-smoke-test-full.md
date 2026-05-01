# 🎯 Запуск Smoke-теста KiloImportService

**Дата**: 2026-05-01  
**Цель**: Проверить, что UI работает с реальным Visary API

---

## 📋 Статус сейчас

| Сервис | Порт | Статус | Комментарий |
|--------|------|--------|-------------|
| Frontend | 5173 | ✅ Запущен | Vite v8.0.10 ready |
| Backend | 5000 | ❌ Не запущен | Ожидает PostgreSQL |
| PostgreSQL (service-db) | 5433 | ❌ Не запущен | Docker Desktop UI required |
| PostgreSQL (visary-db) | 5434 | ❌ Не запущен | Docker Desktop UI required |

---

## ✅ Инструкция по запуску (Docker Desktop UI)

### Шаг 1: Запустить PostgreSQL

1. Открой **Docker Desktop**
2. Перейди в раздел **Containers**
3. Найди контейнеры:
   - `kilo-import-pg-service`
   - `kilo-import-pg-visary`
4. Нажми **Start** для каждого
5. Убедись, что статус → **Running** (зелёный)

### Шаг 2: Запустить Backend

После запуска PostgreSQL (через ~10 секунд после Start):

```powershell
cd "C:\Users\ancye\Downloads\vs code\Alfa\KiloImportService.Api"
dotnet run
```

**Ожидаемый вывод**:
```
[04:XX:XX INF] Applying ImportServiceDb migrations…
[04:XX:XX INF] Now listening on: http://localhost:5000
```

### Шаг 3: Открыть UI в браузере

```
http://localhost:5173
```

---

## 🧪 Smoke-тест сценарий

### 1. Выбор проекта

1. Открой UI (http://localhost:5173)
2. В форме импорта найди Select **"Проект"**
3. Кликни на Select → должен появиться список проектов
4. Проверь DevTools → Network:
   - Запрос: `GET /api/projects/search?q=&limit=50`
   - Ответ: `200 OK` с массивом проектов

### 2. Выбор объекта строительства

1. Выбери любой проект (первый вариант)
2. В Select **"Объект строительства"** должны появиться объекты
3. Проверь DevTools → Network:
   - Запрос: `POST /api/visary/listview/constructionsite/onetomany/Project`
   - Ответ: `200 OK` с массивом объектов

### 3. Импорт "Финмодель"

1. Выбери тип импорта: **"Финмодель"**
2. В поле **"Тип отделки"** загрузи Excel файл со значением "Черновая"
3. Нажми **"Запустить импорт"**
4. Проверь DevTools → Network:
   - Запрос: `POST /api/imports` (multipart/form-data)
   - Ответ: `202 Accepted` с sessionId

### 4. Проверка в БД

1. Подключись к PostgreSQL visary-db:
   ```bash
   docker exec -it kilo-import-pg-visary psql -U visary -d visary_webapi_db
   ```
2. Выполни запрос:
   ```sql
   SELECT "Id", "Title", "FinishingMaterialId" 
   FROM "Data"."ConstructionSite" 
   WHERE "Id" = <твой_siteId>;
   ```
3. Значение `FinishingMaterialId` должно быть **3** (Черновая)

---

## 🔍 Проверка логов

### Backend логи

В консоли `dotnet run` должно быть:

```
[04:XX:XX INF] Session {sessionId}: starting VALIDATE stage
FinModelImportMapper.ValidateAsync: siteId={siteId}, rows=1
FinModelImportMapper.ValidateAsync: ConstructionSite query completed siteFound=True
FinModelImportMapper.ValidateAsync: completed mappedRows=1 fileErrors=0
```

### Frontend логи

В DevTools → Console должен быть:

```
[useProjects] load() → idle
[useProjects] load() → loading
[useProjects] load() → success
[useSites] load() → idle
[useSites] load() → loading
[useSites] load() → success
```

---

## ⚠️ Частые проблемы

### Проблема 1: Backend не отвечает

**Симптом**: `curl http://localhost:5000/health` → Connection refused

**Решение**:
1. Проверь статус PostgreSQL в Docker Desktop UI
2. Подожди ~10 секунд после Start
3. Перезапусти backend: `dotnet run`

### Проблема 2: Select пустой

**Симптом**: В UI Select "Проект" не показывает опции

**Решение**:
1. Проверь токен в `.env.local`
2. Проверь DevTools → Network → `/api/projects/search`
3. Проверь логи backend на ошибки 401/403

### Проблема 3: Токен истёк

**Симптом**: `401 Unauthorized` в логах backend

**Решение**:
1. Обнови токен в `.env.local`
2. Перезапусти frontend: `npm run dev`

---

## 📊 Чек-лист Smoke-теста

- [ ] Все контейнеры PostgreSQL статус **Running**
- [ ] Backend запущен и отвечает на `http://localhost:5000/health`
- [ ] Frontend запущен на `http://localhost:5173`
- [ ] Select "Проект" загружает список проектов
- [ ] Select "Объект строительства" загружает объекты
- [ ] Импорт "Финмодель" проходит без ошибок
- [ ] В БД поле `FinishingMaterialId` обновилось
- [ ] Логи backend показывают успешную валидацию

---

## 📝 Если всё работает

После успешного прохождения smoke-теста:

1. Запусти полный цикл через Docker Compose (раздел 2)
2. Добавь E2E тесты для CI/CD
3. Примени миграции к прод-среде

---

**Версия**: 1.0  
**Дата**: 2026-05-01  
**Автор**: Kilo
