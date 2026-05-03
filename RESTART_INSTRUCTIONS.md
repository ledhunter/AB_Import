# Инструкция по перезапуску сервисов через Docker Desktop UI

## ⚠️ Важно
Docker CLI не работает из-за несовместимости API версий. Используйте Docker Desktop UI.

## Шаг 1: Остановить все контейнеры
1. Откройте Docker Desktop
2. Перейдите в раздел **Containers**
3. Найдите `kilo-import-*` контейнеры
4. Нажмите **Stop** для каждого:
   - `kilo-import-pg-service` — служебная БД
   - `kilo-import-pg-visary` — целевая БД Visary
   - `kilo-import-backend` — backend API
   - `kilo-import-frontend` — frontend UI

## Шаг 2: Перезапустить БД
1. Найдите `kilo-import-pg-service` и нажмите **Start**
2. Подождите 10-15 секунд пока БД инициализируется
3. Найдите `kilo-import-pg-visary` и нажмите **Start**
4. Подождите 10-15 секунд пока БД инициализируется

## Шаг 3: Обновить токен и перезапустить Backend
1. Откройте терминал
2. Выполните:
   ```bash
   cd "C:\Users\ancye\Downloads\vs code\Alfa\KiloImportService.Api"
   dotnet build
   dotnet run
   ```
3. Проверьте логи на наличие ошибок connection to PostgreSQL

## Шаг 4: Перезапустить Frontend
1. Откройте терминал
2. Выполните:
   ```bash
   cd "C:\Users\ancye\Downloads\vs code\Alfa\KiloImportService.Web"
   npm run dev
   ```
3. Frontend автоматически подхватит обновленный токен из .env

## Шаг 5: Проверка
1. Backend должен запуститься на `http://localhost:5000`
2. Frontend должен запуститься на `http://localhost:5173`
3. Откройте http://localhost:5173 в браузере
4. Проверьте что проекты загружаются корректно

## Альтернатива: Полный перезапуск через docker-compose
Если CLI заработает:
```bash
docker compose down -v
docker compose up -d --build
```

---

**Дата**: 2026-05-03  
**Автор**: Kilo
