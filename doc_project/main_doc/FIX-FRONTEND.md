# Ошибка: "connectionfailure" на http://localhost:5173

## Причина

Frontend (Vite) не может запуститься — либо:
1. Docker Desktop не запущен (для Docker-режима)
2. Нет установки Node.js/dependencies (для локального запуска)
3. Ошибка TypeScript/Build (невозможно сбилдить приложение)

## Решение 1: Локальный запуск (без Docker)

### Шаг 1: Установи зависимости

```bash
cd "C:\Users\ancye\Downloads\vs code\Alfa\KiloImportService.Web"
npm install
```

### Шаг 2: Запусти dev-сервер

```bash
npm run dev
```

### Шаг 3: Открой в браузере

```
http://localhost:5173
```

---

## Решение 2: Docker-режим

### Шаг 1: Запусти Docker Desktop

Открой Docker Desktop и дождись статуса **Running**.

### Шаг 2: Запусти контейнеры

```bash
cd "C:\Users\ancye\Downloads\vs code\Alfa"
docker compose up -d frontend
```

---

## 💡 Рекомендация

Сначала пробуй **локальный запуск** (`npm run dev`) — проще отлаживать ошибки.

Docker используй, когда нужна готовая инфраструктура (PostgreSQL, Backend).

---

## 🔍 Проверка

1. Открой терминал в папке `KiloImportService.Web`
2. Выполни `npm run dev`
3. Если видишь `http://localhost:5173` — ок
4. Если ошибка — пришли текст ошибки
