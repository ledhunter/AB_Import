# 📝 История изменений проекта KiloImportService

**Дата последнего обновления**: 2026-05-01  
**Автор**: Kilo  
**Версия**: 1.0

---

## 📊 Общая статистика

- **Backend тесты**: 64/69 пройдено (92.8%)
- **Frontend тесты**: 28/28 пройдено (100%)
- **Docker CLI**: Не работает (требуется Docker Desktop UI)
- **Статус**: Готов к запуску через Docker Desktop UI

---

## 🔄 Последние изменения (01.05.2026)

### Backend

#### 1. Исправление ProjectsCacheService.cs

**Проблема 1**: Неверная логика поиска при пустом запросе

- **Файл**: `KiloImportService.Api/Domain/Projects/ProjectsCacheService.cs:121-127`
- **До**: Возвращались все проекты при пустом запросе
- **После**: Возвращается пустой список

**Проблема 2**: Метод `Contains()` вместо `EF.Functions.Like()`

- **Файл**: `KiloImportService.Api/Domain/Projects/ProjectsCacheService.cs:134-143`
- **До**: Плохая производительность и непредсказуемое поведение
- **После**: Используется `EF.Functions.Like()` для корректного SQL

#### 2. Улучшение FinModelImportMapper.cs

- Добавлено детальное логирование процесса валидации:
  - Логирование начала валидации
  - Логирование запроса к ConstructionSite
  - Прогресс обработки строк (каждые 500 строк)
  - Итоговый результат валидации

#### 3. Улучшение ImportPipeline.cs

- Добавлены логи для этапов валидации:
  - Начало этапа VALIDATE
  - Вызов mapper.ValidateAsync
  - Результат валидации

#### 4. Удаление FileSha256 ограничения

- Убран `FileSha256` из модели `ImportSession`
- Удалён уникальный индекс `UX_ImportSession_TypeAndSha`
- Теперь один и тот же файл можно загружать несколько раз по одному типу импорта
- **Миграция**: `20260430213808_RemoveFileSha256Constraint`

#### 5. Очистка старых миграций

- Удалены устаревшие миграции:
  - `20260429084812_Initial`
  - `20260429170000_AddCachedProjects`

### Frontend

#### 1. Улучшение UI отображения проектов

- Убрано поле `code` (IdentifierKK/ZPLM) из отображения в Select
- Теперь показывается только название проекта: `Тест ФМ - Опус` вместо `Тест ФМ - Опус (5634576748978)`
- **Файлы**: `types/listView.ts`, `services/listView/entities/projects.ts`, `services/projectsBackendApi.ts`, `components/ImportForm/ImportForm.tsx`

### Тесты

- Обновлен тест `SearchAsync_EmptyQuery` на ожидание пустого списка

---

## 📚 Добавленная документация

### Код и архитектура

1. **25-ui-project-options-display.md** — UI-изменения для отображения проектов
2. **26-troubleshooting.md** — Решение проблем: запуск backend, проверка БД, отладка
3. **27-checklists.md** — Чек-листы: запуск, добавление маппера, деплой
4. **28-faq.md** — Частые вопросы: пустой Select, бэкок, обновление токена
5. **29-backend-issues-found-and-fixed.md** — Найденные проблемы в ProjectsCacheService
6. **30-code-analysis-result.md** — Полный анализ кода и тестов
7. **31-smoke-test-instructions.md** — Инструкция по запуску backend
8. **32-smoke-test-full.md** — Полный сценарий smoke-теста
9. **33-docker-cli-troubleshooting.md** — Объяснение ограничения Docker CLI
10. **34-full-run-instructions.md** — Дополнительная инструкция по запуску
11. **35-run-through-docker-ui.md** — Инструкция по запуску через Docker Desktop UI
12. **36-docker-desktop-issue.md** — Документирование проблемы с Docker Desktop

### Инструкции и скрипты

1. **FIX-FRONTEND.md** — Решение проблем с запуском frontend
2. **START.md** — Полная инструкция по запуску полного цикла
3. **scripts/start-full-cycle.ps1** — Автозапуск всех сервисов

---

## 📋 Статус сервисов

| Сервис | Порт | Статус | Примечание |
|--------|------|--------|------------|
| Backend | 5000 | ⏳ Ожидает PostgreSQL | Требует Docker Desktop UI |
| Frontend | 5173 | ✅ Готов | Запуск через `npm run dev` |
| PostgreSQL | 5433/5434 | ⏳ Ожидает | Требует Docker Desktop UI |
| Backend тесты | - | ✅ 64/69 | 0 фейлов |
| Frontend тесты | - | ✅ 28/28 | 0 фейлов |

---

## ⚠️ Известные ограничения

### Docker CLI не работает

**Проблема**: Docker CLI не может подключиться к daemon из-за несовместимости API версий.

**Решение**: Используй Docker Desktop UI для запуска контейнеров.

**Документация**: `doc_project/36-docker-desktop-issue.md`

---

## 🎯 Следующие шаги

### 1. Smoke-тест (высокий приоритет)

1. Запустить PostgreSQL через Docker Desktop UI
2. Запустить Backend через `dotnet run`
3. Открыть `http://localhost:5173` в браузере
4. Пройти полный цикл импорта

### 2. Интеграция Backend + Frontend через Docker (средний приоритет)

1. Поднять PostgreSQL через docker-compose
2. Запустить backend `dotnet run`
3. Проверить полный цикл: UI → Backend → Visary API → БД

### 3. E2E тесты (низкий приоритет)

1. Выбрать фреймворк (Playwright / Cypress)
2. Написать E2E тесты для полного цикла импорта

---

## 🔍 Коммиты

```
19518f5 Merge pull request #4 from ledhunter/room_branch
19967be Добавлен метод поиска объектов по проекту. Получение и начальная обработка файла импорта. во время последних тестирований, после добавления типа импорта "Финмодель" возникли проблемы с получением информации по проекту (и далее) из-за трудностей с поднятием бэка. проблема не решена
2a9c49e Merge pull request #3 from ledhunter/room_branch
3ac66fa доработан метод получения и кэширования проектов
7cecebb Merge pull request #2 from ledhunter/my-feature-new
```

---

## 📦 Зависимости

### Backend

- **EF Core**: 9.0.4
- **Npgsql**: 9.0.4
- **XUnit**: xUnit.net 3.1.4
- **ClosedXML**: для тестов XLSX

### Frontend

- **React**: 18-19
- **TypeScript**: latest
- **Vite**: 8.0.10
- **@alfalab/core-components**: оригинальные компоненты Альфа-Банка

---

## 💡 Полезные команды

### Backend

```bash
# Сборка
cd "C:\Users\ancye\Downloads\vs code\Alfa\KiloImportService.Api"
dotnet build

# Запуск tests
cd "C:\Users\ancye\Downloads\vs code\Alfa\KiloImportService.Api.Tests"
dotnet test

# Запуск backend (после PostgreSQL)
cd "C:\Users\ancye\Downloads\vs code\Alfa\KiloImportService.Api"
dotnet run
```

### Frontend

```bash
# Запуск dev-сервера
cd "C:\Users\ancye\Downloads\vs code\Alfa\KiloImportService.Web"
npm run dev

# Сборка
npm run build

# Линтинг
npm run lint
```

---

**Версия**: 1.0  
**Дата**: 2026-05-01  
**Автор**: Kilo
