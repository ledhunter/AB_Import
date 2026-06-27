# 📊 Руководство по импорту "Финмодель"

## Быстрый старт

### 1. Подготовка Excel файла

Создайте Excel файл с колонкой **"Тип отделки"**:

| Тип отделки |
|-------------|
| Черновая    |

**Допустимые значения**:
- Черновая (ID=3)
- Предчистовая (ID=2)
- Чистовая (ID=1)

### 2. Запуск импорта через UI

1. Откройте сервис: `http://localhost:5174`
2. Выберите **"Финмодель"** в поле "Тип импорта"
3. Выберите проект из списка
4. Выберите объект строительства
5. Нажмите на область "Загрузите файл" и выберите Excel
6. Нажмите **"Запустить импорт"**
7. Дождитесь завершения валидации
8. Нажмите **"Применить"**

### 3. Проверка результата

```sql
SELECT "Id", "Title", "FinishingMaterialId" 
FROM "Data"."ConstructionSite" 
WHERE "Id" = <ваш_id>;
```

## Структура проекта

### Backend

```
KiloImportService.Api/
├── Domain/
│   └── Mapping/
│       └── FinModelImportMapper.cs      # Маппер для типа "finmodel"
├── Data/
│   └── Visary/
│       └── Entities/
│           └── ConstructionSite.cs      # Сущность с полем FinishingMaterialId
└── Controllers/
    └── ImportTypesController.cs         # Регистрация типа "Финмодель"
```

### Frontend

```
KiloImportService.Web/
├── src/
│   ├── components/
│   │   ├── FileUpload/
│   │   │   └── FileUpload.tsx           # Компонент загрузки файла
│   │   └── ImportForm/
│   │       └── ImportForm.tsx           # Форма выбора проекта/объекта
│   └── services/
│       └── importsService.ts            # API клиент с fallback mock
```

### Тесты

```
KiloImportService.Api.Tests/
└── Mapping/
    └── FinModelImportMapperTests.cs     # Unit-тесты маппера
```

## Технические детали

### Справочник типов отделки

Маппинг хранится в `FinModelImportMapper.GetFinishingMaterialId()`:

```csharp
private static int? GetFinishingMaterialId(string title)
{
    return title.Trim() switch
    {
        "Черновая" => 3,
        "Предчистовая" => 2,
        "Чистовая" => 1,
        _ => null
    };
}
```

### Альтернативные названия колонок

Поддерживаются следующие варианты (регистр не важен):
- "Тип отделки"
- "FinishingType"
- "Finishing"

### Процесс валидации

1. ✅ Проверка наличия `siteId`
2. ✅ Проверка существования объекта в БД
3. ✅ Поиск колонки "Тип отделки"
4. ✅ Проверка непустого значения
5. ✅ Валидация по справочнику

### Процесс применения

1. Загрузка объекта `ConstructionSite` из БД
2. Обновление `FinishingMaterialId`
3. Сохранение через `SaveChangesAsync`

## Устранение неполадок

### Backend не запускается

**Проблема**: Ошибка подключения к PostgreSQL

**Решение**: Проверьте connection string в `appsettings.json`:

```json
{
  "ConnectionStrings": {
    "ServiceDb": "Host=localhost;Database=kilo_import;...",
    "VisaryDb": "Host=localhost;Database=visary;..."
  }
}
```

### Тип "Финмодель" не отображается

**Проблема**: Backend недоступен

**Решение**: Используется fallback mock. Тип будет доступен, но импорт не сработает без backend.

### Ошибка "Объект не найден"

**Проблема**: Неправильный `siteId`

**Решение**: Проверьте, что объект существует в БД:

```sql
SELECT * FROM "Data"."ConstructionSite" WHERE "Id" = <id> AND "Hidden" = false;
```

### Ошибка "Неизвестный тип отделки"

**Проблема**: Значение не соответствует справочнику

**Решение**: Используйте точные названия:
- Черновая
- Предчистовая
- Чистовая

## Запуск тестов

### Backend тесты

```bash
cd KiloImportService.Api.Tests
dotnet test --filter "FullyQualifiedName~FinModelImportMapperTests"
```

**Ожидаемый результат**: 8 тестов пройдено ✅

### Интеграционное тестирование

1. Подготовьте тестовый Excel файл
2. Создайте тестовый объект в Visary
3. Запустите импорт через UI
4. Проверьте обновление в БД

## Расширение функционала

### Добавление нового параметра

**Пример**: Добавление "Класс энергоэффективности"

1. **Добавьте поле в сущность**:

```csharp
// ConstructionSite.cs
public int? EnergyEfficiencyClassId { get; set; }
```

2. **Добавьте алиасы в маппер**:

```csharp
// FinModelImportMapper.cs
private static readonly string[] EnergyClassAliases = 
    ["Класс энергоэффективности", "EnergyClass"];
```

3. **Обновите валидацию**:

```csharp
var energyClassCol = row.Cells.Keys.FirstOrDefault(k =>
    EnergyClassAliases.Any(a => a.Equals(k, StringComparison.OrdinalIgnoreCase))
);
```

4. **Обновите применение**:

```csharp
site.EnergyEfficiencyClassId = energyClassId;
```

5. **Добавьте тесты**

## Полезные ссылки

- 📚 [Полная документация](./doc_project/23-finmodel-import.md)
- 🏗️ [Обновление через CRUD API](./doc_project/22-update-finishing-material.md)
- 📋 [Общая документация проекта](./doc_project/README.md)

## Поддержка

При возникновении проблем:
1. Проверьте логи backend (консоль dotnet run)
2. Проверьте логи frontend (DevTools → Console)
3. Проверьте подключение к БД
4. Обратитесь к документации в `doc_project/`

---

**Версия**: 1.0  
**Дата**: 2026-04-30  
**Автор**: Cascade AI
