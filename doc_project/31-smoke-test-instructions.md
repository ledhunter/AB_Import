# 🏃‍♂️ Инструкция по запуску backend и PostgreSQL

**Дата**: 2026-05-01  
**Статус**: Ожидает запуска

---

## ⚠️ Проблема

Backend не запускается, потому что PostgreSQL (service-db) не запущен.

---

## ✅ Решение: Docker Desktop UI

### Шаг 1: Запустить PostgreSQL

1. Открой **Docker Desktop**
2. Перейди в раздел **Containers**
3. Найди контейнеры:
   - `kilo-import-pg-service` — служебная БД (порт 5433)
   - `kilo-import-pg-visary` — целевая БД Visary (порт 5434)
4. Для каждого контейнера нажми **Start**
5. Убедись, что статус → **Running**

### Шаг 2: Проверить порты

Для каждого контейнера:
1. Кликни на контейнер → вкладка **Ports**
2. Убедись, что порты отображаются:
   - `5433/tcp` → `0.0.0.0:5433`
   - `5434/tcp` → `0.0.0.0:5434`

### Шаг 3: Запустить Backend

После того как PostgreSQL статус **Running**:

```bash
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
```bash
curl http://localhost:5000/health
```

**Ожидаемый ответ**:
```json
{"status":"Healthy","services":[]}
```

---

## 🌐 Доступные URL после запуска

| Сервис | URL | Порт | Статус |
|--------|-----|------|--------|
| Frontend (Vite) | http://localhost:5173 | 5173 | ✅ Запущен |
| Backend API | http://localhost:5000 | 5000 | ⏳ Ожидает |
| PostgreSQL (service-db) | localhost:5433 | 5433 | ⏳ Ожидает |
| PostgreSQL (visary-db) | localhost:5434 | 5434 | ⏳ Ожидает |

---

## 🔍 Если контейнеры не найдены

### Вариант A: Запустить через docker-compose

```bash
cd "C:\Users\ancye\Downloads\vs code\Alfa"
docker compose up -d pg-service pg-visary
```

### Вариант B: Проверить все контейнеры

```bash
docker compose ps
```

---

## 📌 После успешного запуска

1. Открой http://localhost:5173 в браузере
2. Выбери тип импорта: **"Финмодель"**
3. Выбери проект из списка
4. Выбери объект строительства
5. Загрузи Excel файл с колонкой "Тип отделки"
6. Нажми **"Запустить импорт"**

---

**Версия**: 1.0  
**Дата**: 2026-05-01  
**Автор**: Kilo
