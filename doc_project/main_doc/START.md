# Инструкция по запуску полного цикла KiloImportService

## ⚠️ Проблема: Docker Desktop CLI не подключается

**Симптом**: `failed to connect to the docker API at npipe:////./pipe/dockerDesktopLinuxEngine`

**Решение**: Использовать Docker Desktop UI вместо CLI.

---

## 🚀 Запуск через Docker Desktop UI

### Шаг 1: Запустить PostgreSQL контейнеры

1. Открой Docker Desktop
2. Перейди в раздел **Containers**
3. Найди:
   - `kilo-import-pg-service` — служебная БД
   - `kilo-import-pg-visary` — целевая БД Visary
4. Нажми **Start** для каждого

### Шаг 2: Собрать и запустить Backend

```bash
cd "C:\Users\ancye\Downloads\vs code\Alfa\KiloImportService.Api"
dotnet build
dotnet run
```

### Шаг 3: Собрать и запустить Frontend

```bash
cd "C:\Users\ancye\Downloads\vs code\Alfa\KiloImportService.Web"
npm install
npm run dev
```

---

## 🌐 Доступные URL'ы

| Сервис | URL | Порт |
|--------|-----|------|
| UI | http://localhost:5173 | 5173 |
| Backend API | http://localhost:5000 | 5000 |
| Swagger | http://localhost:5000/swagger | 5000 |
| PostgreSQL (service-db) | localhost:5433 | 5433 |
| PostgreSQL (visary-db) | localhost:5434 | 5434 |

---

## 🔍 Проверка статуса контейнеров

Если CLI не работает:

1. **Docker Desktop UI** → Containers → ищи `kilo-import-*`
2. Используй кнопки **Start/Stop/Restart** для управления
3. Проверь логи через кнопку **Logs**

---

## 🧪 Тестирование

1. **Backend логи** (в консоли при `dotnet run`):
   ```
   [HH:mm:ss INF] Starting KiloImportService.Api...
   [HH:mm:ss INF] Applying ImportServiceDb migrations...
   ```

2. **Swagger** открой в браузере → проверь эндпоинты

3. **UI** открой http://localhost:5173 → выбери тип импорта "Финмодель"

---

## 💡 Полезные команды (когда CLI работает)

```bash
# Полный запуск
docker compose up -d --build

# Логи backend
docker compose logs -f backend

# Остановка
docker compose down

# Полный сброс (включая БД)
docker compose down -v
```

---

## 📌 Важно

- Frontend Dockerfile настроен на **dev-режим** (Vite с HMR)
- Backend ожидает `health` endpoint для healthcheck
- Visary DB init-скрипты (`01-schema.sql`, `02-missing-roots.sql`, `03-seed-data.sql`) выполняются один раз при создании контейнера
