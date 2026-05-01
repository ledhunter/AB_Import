# 🚀 Автозапуск полного цикла KiloImportService

> **Запусти этот скрипт в PowerShell** (Ctrl+Shift+P → New PowerShell Window)

---

## ⚠️ Предварительные требования

1. **Docker Desktop** установлен и запущен
2. В `KiloImportService.Web/.env.local` задан актуальный токен Visary
3. В `KiloImportService.Api/appsettings.json` заданы правильные констрейнты

---

## 📋 Инструкция

### Шаг 1: Запустить PostgreSQL (через Docker Desktop UI)

1. Открой Docker Desktop
2. Containers → найди:
   - `kilo-import-pg-service`
   - `kilo-import-pg-visary`
3. Start для каждого

### Шаг 2: Запустить все сервисы (PowerShell)

```powershell
# Определи корневую папку
$root = "C:\Users\ancye\Downloads\vs code\Alfa"

# 1. Запустить backend
Write-Host "🚀 Запуск backend..." -ForegroundColor Green
Set-Location "$root\KiloImportService.Api"
Start-Process dotnet -ArgumentList "run" -WindowStyle Normal -Wait

# 2. Запустить frontend (в новой консоли)
Write-Host "🚀 Запуск frontend..." -ForegroundColor Green
Start-Process powershell -ArgumentList "-NoExit", "-Command", "cd '$root\KiloImportService.Web'; npm run dev"
```

---

## 🛑 Остановка сервисов

### Способ A: Через PowerShell (Ctrl+C)

- В консоли backend: `Ctrl+C`
- В консоли frontend: `Ctrl+C`

### Способ B: Через Docker Desktop UI

- Containers → найди контейнер
- Нажми **Stop**

---

## 🔧 Ошибка: PostgreSQL не запущен

**Симптом**: Backend падает с `Connection refused` на порт 5433.

**Решение**:
1. Docker Desktop → Containers
2. `kilo-import-pg-service` → Start
3. Подожди ~10 секунд
4. Запусти backend снова: `dotnet run`

---

## ✅ Проверка

После запуска:

```bash
# Backend health
curl http://localhost:5000/health

# Swagger
open http://localhost:5000/swagger

# Frontend
open http://localhost:5173
```

---

**Версия**: 1.0  
**Дата**: 2026-05-01  
**Автор**: Kilo
