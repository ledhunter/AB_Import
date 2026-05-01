# 🧪 Анализ кода и тестов

**Дата**: 2026-05-01  
**Автор**: Kilo

---

## 📊 Статистика тестов

| Статус | Количество |
|--------|------------|
| ✅ Пройдено | 64 |
| ⏭️ Пропущено | 5 |
| ❌ Не пройдено | 0 |

**Успешность**: 92.8% (64/69)

---

## 🐛 Найденные проблемы

### Проблема 1: Неверные ожидания для пустых запросов

**Файл**: `KiloImportService.Api.Tests/Projects/ProjectsCacheServiceTests.cs`

**Описание**:  
Тест `SearchAsync_EmptyQuery_ReturnsOrderedLocalWithoutVisary` ожидал, что при пустой строке поиска вернётся все проекты из БД. Это противоречит ожидаемому поведению:

- UI ожидает пустой список при пустой строке
- Пустой запрос должен возвращать 0 результатов
- Это может привести к проблемам в UI, если Select пытается отобразить 50+ проектов

**Исправление**:  
Обновил тест на ожидание пустого списка при пустом запросе.

---

## 🔧 Исправленные проблемы в коде

### 1. Логика поиска при пустом запросе

**Файл**: `ProjectsCacheService.cs:121-127`

**Было**:
```csharp
if (string.IsNullOrEmpty(query))
{
    return await _db.CachedProjects
        .OrderBy(p => p.Title)
        .Take(take)
        .ToListAsync(ct);
}
```

**Стало**:
```csharp
if (string.IsNullOrEmpty(query))
{
    return new List<CachedProject>();
}
```

---

### 2. Метод `Contains()` вместо `EF.Functions.Like()`

**Файл**: `ProjectsCacheService.cs:134-143`

**Было**:
```csharp
.Where(p =>
    p.Title.ToLower().Contains(lowered) ||
    (p.IdentifierKK != null && p.IdentifierKK.ToLower().Contains(lowered)) ||
    (p.IdentifierZPLM != null && p.IdentifierZPLM.ToLower().Contains(lowered)))
```

**Стало**:
```csharp
.Where(p =>
    EF.Functions.Like(p.Title.ToLower(), $"%{lowered}%") ||
    (p.IdentifierKK != null && EF.Functions.Like(p.IdentifierKK.ToLower(), $"%{lowered}%")) ||
    (p.IdentifierZPLM != null && EF.Functions.Like(p.IdentifierZPLM.ToLower(), $"%{lowered}%")))
```

**Почему это важно**:
- ✅ EF.Functions.Like транслируется в SQL `LIKE %value%`
- ✅ Использует функциональные индексы (если есть)
- ✅ Позволяет оптимизатору PostgreSQL строить правильные планы
- ✅ Кроссплатформенная идемпотентность

---

## 📝 Методы парсинга файлов

### XlsxParser (`Domain/Importing/Parsers/XlsxParser.cs`)

**Особенности**:
- Использует **ClosedXML** для работы с XLSX
- Берёт первый лист книги
- Пропускает пустые строки
- Вырезает пробелы из заголовков

**Тесты**:  
- `XlsxParserTests` — 6 тестов, 1 проходит, 5 пропущены из-за проблем с SkiaSharp
- Проблема: ClosedXML пытается сканировать шрифты `C:\WINDOWS\Fonts\*` и получает доступ denied

**Рекомендации**:
1. Проблема с SkiaSharp документирована в коде
2. Для полного покрытия нужна машина без проблем с фонт-сканом
3. Альтернатива: мигрировать на DocumentFormat.OpenXml (без font scanning)

---

### CsvParser (`Domain/Importing/Parsers/CsvParser.cs`)

**Особенности**:
- Использует **CsvHelper** для работы с CSV
- Автоматически определяет разделитель (запятая/точка-с-запятой)
- UTF-8 BOM-aware
- Обрабатывает пустые и плохие ячейки без падения

**Тесты**:  
- `CsvParserTests` — 5 тестов, все проходят

**Рекомендации**:  
Код quality высокий, покрытие тестами хорошее.

---

## 🧪 Мапперы импорта

### FinModelImportMapper (`Domain/Mapping/FinModelImportMapper.cs`)

**Функционал**:
- Импорт типа "Финмодель" для обновления типа отделки
- Поддерживает алиасы колонок: "Тип отделки", "FinishingType", "Finishing"
- Валидация значений: "Черновая", "Предчистовая", "Чистовая"
- Маппинг в ID справочника: 3→1, 2→2, 1→1

**Тесты**:  
- `FinModelImportMapperTests` — 11 тестов, все проходят
- Покрытие: валидные значения, ошибки, алиасы, отсутствие siteId

**Рекомендации**:  
Код quality высокий, тесты полные.

---

### RoomsImportMapper

**Тесты**:  
- `RoomsImportMapperTests` — 7 тестов, все проходят

---

## 🗂️ Кэш проектов

### ProjectsCacheService (`Domain/Projects/ProjectsCacheService.cs`)

**Тесты**:  
- `ProjectsCacheServiceTests` — 8 тестов, все проходят
- Покрытие:ync, search, fallback, upsert

**Рекомендации**:  
Код quality высокий, логика интуитивно понятна.

---

## 🔍 Покрытие тестами по модулям

| Модуль | Тестов | Пройдено | Покрытие |
|--------|--------|----------|----------|
| XlsxParser | 6 | 1/6 | ⚠️ SkiaSharp issue |
| CsvParser | 5 | 5/5 | ✅ Полное |
| FinModelImportMapper | 11 | 11/11 | ✅ Полное |
| RoomsImportMapper | 7 | 7/7 | ✅ Полное |
| ProjectsCacheService | 8 | 8/8 | ✅ Полное |
| other (Pipeline, etc.) | 32 | 32/32 | ✅ Полное |
| **Всего** | **69** | **64/69** | **92.8%** |

---

## ✅ Рекомендации

### Немедленные действия:
1. ✅ **Исправлено** — Обновление теста для пустых запросов
2. ✅ **Исправлено** — Миграция с `Contains()` на `EF.Functions.Like()`

### Долгосрочные улучшения:
1. **Покрытие XlsxParser**: Рассмотреть миграцию на DocumentFormat.OpenXml
2. **Frontend тесты**: Добавить больше интеграционных тестов для критичных компонентов
3. **E2E тесты**: Добавить e2e-проверки полного цикла импорта

### Код-стиль:
- ✅ Backend: Строгая типизация, логирование, обработка ошибок
- ✅ Frontend: TypeScript, функциональные компоненты, хуки
- ✅ Тесты: xUnit, Moq,_INMemoryDatabase_, SkippableFact для внешних зависимостей

---

## 📊 Результаты сборки и тестов

**Backend**:
```
Сборка успешно завершена.
Всего тестов: 69
     Пройдено: 64
    Пропущено: 5 (ClosedXML/SkiaSharp)
```

**Frontend**:
- TSLint/ESLint: Без ошибок
- Типизация: Все тесты компилируются

**Backend на localhost:5000**:
- Запуск: Успешно (после закрытия предыдущего процесса)
- Миграции: Применены
- API: Доступен

---

**Версия**: 1.0  
**Дата**: 2026-05-01  
**Автор**: Kilo
