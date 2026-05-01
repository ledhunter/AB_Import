# ⚠️ Docker Desktop CLI не подключается

**Дата**: 2026-05-01  
**Статус**: Решение через Docker Desktop UI

---

## 🐛 Проблема

Docker Desktop CLI не подключается к daemon:

```
failed to connect to the docker API at npipe:////./pipe/dockerDesktopLinuxEngine
```

**Возможные причины**:
1. Docker Desktop запущен, но daemon ещё не инициализировался
2. Проблема с правами доступа к npipe
3. Docker Desktop ещё не полностью стартовал

---

## ✅ Решение: Использовать Docker Desktop UI

### Шаг 1: Запустить контейнеры через UI

1. Открой **Docker Desktop**
2. Перейди в раздел **Containers**
3. Найди контейнеры:
   - `kilo-import-pg-service`
   - `kilo-import-pg-visary`
4. Для каждого контейнера нажми **Start**
5. Дождись статуса **Running** (зелёный значок)

### Шаг 2: Проверить порты

Для каждого контейнера:
1. Кликни на контейнер
2. Вкладка **Ports**
3. Убедись, что отображаются порты:
   - `5433/tcp` → `0.0.0.0:5433`
   - `5434/tcp` → `0.0.0.0:5434`

---

## 🚀 Запуск Backend и Frontend

### Backend

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

### Frontend (уже запущен)

Frontend уже работает на `http://localhost:5173`.

---

## 📋 Статус сервисов

| Сервис | Порт | Статус | Способ запуска |
|--------|------|--------|----------------|
| PostgreSQL (service-db) | 5433 | ⏳ Ожидает Docker Desktop UI | Через UI |
| PostgreSQL (visary-db) | 5434 | ⏳ Ожидает Docker Desktop UI | Через UI |
| Backend API | 5000 | ⏳ Ожидает PostgreSQL | `dotnet run` |
| Frontend UI | 5173 | ✅ Запущен | `npm run dev` |

---

## 🔍 Проблемы при запуске backend

### Ошибка: Connection refused на 5433

**Симптом**:
```
Npgsql.PostgresException (0x80004005): Connection refused
```

**Решение**:
1. Проверь статус контейнера `kilo-import-pg-service` в Docker Desktop UI
2. Убедись, что статус **Running**
3. Подожди ~10 секунд после Start
4. Попробуй снова: `dotnet run`

---

## 📌 Важно

- **Docker Desktop UI** — единственный надёжный способ управлять контейнерами сейчас
- CLI работает только после полной инициализации daemon Docker Desktop
- Время запуска PostgreSQL: ~10-15 секунд после Start в UI

---

**Версия**: 1.0  
**Дата**: 2026-05-01  
**Автор**: Kilo
