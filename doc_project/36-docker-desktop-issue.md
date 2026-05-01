# 🔧 Техническая проблема с Docker Desktop

**Дата**: 2026-05-01  
**Статус**: Docker Desktop запущен, но CLI не может подключиться

---

## 🐛 Проблема

Docker Desktop запущен, но CLI показывает ошибку:

```
request returned 500 Internal Server Error for API route and version 
http://%2F%2F.%2Fpipe%2FdockerDesktopLinuxEngine/v1.54/containers/json
```

**Причина**: Несовместимость API версий между Docker CLI и Docker Desktop демоном.

---

## ✅ Решение: Ручной запуск контейнеров через Docker Desktop UI

Поскольку Docker CLI не работает, **обязательно используй Docker Desktop UI** для запуска контейнеров.

---

## 🚀 Пошаговая инструкция

### 1. Открой Docker Desktop

- Иконка Docker в трее Windows → кликни
- Или найди "Docker Desktop" в меню Пуск

### 2. Убедись что Docker Desktop running

Внизу интерфейса должно быть:
```
Docker Desktop is running
```

### 3. Запусти PostgreSQL контейнеры

1. Перейди во вкладку **Containers**
2. Найди контейнеры:
   - `/kilo-import-pg-service-1`
   - `/kilo-import-pg-visary-1`
3. Нажми кнопку **Start** (или **Restart**) для каждого
4. Подожди ~10 секунд
5. Убедись, что статус стал **Running** (зелёный)

### 4. Проверь порты

Для каждого контейнера:
1. Кликни на контейнер
2. Вкладка **Ports**
3. Убедись, что отображаются:
   - `5433/tcp` → `0.0.0.0:5433` (service-db)
   - `5434/tcp` → `0.0.0.0:5434` (visary-db)

### 5. Запусти Backend

После запуска PostgreSQL (через ~10 секунд):

```powershell
cd "C:\Users\ancye\Downloads\vs code\Alfa\KiloImportService.Api"
dotnet run
```

### 6. Проверь Backend

В новой консоли:
```powershell
curl http://localhost:5000/health
```

Ожидаемый ответ:
```json
{"status":"Healthy","services":[]}
```

### 7. Проверь Frontend

Frontend уже запущен на `http://localhost:5173`.

---

## 📌 Важно

- CLI не работает из-за несовместимости API версий
- Docker Desktop UI — единственный надёжный способ управления контейнерами
- После запуска контейнеров через UI, backend будет работать корректно

---

**Версия**: 1.0  
**Дата**: 2026-05-01  
**Автор**: Kilo
