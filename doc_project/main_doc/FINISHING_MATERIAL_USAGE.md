# 🎨 Обновление типа отделки - Краткая инструкция

## Быстрый старт

### 1. Импорт функции

```typescript
import { updateFinishingMaterial } from '@/services/visaryCrud';
```

### 2. Обновление типа отделки

```typescript
// Обновить тип отделки объекта строительства
await updateFinishingMaterial(7791, 'Черновая');
```

## Доступные типы отделки

| Название       | ID |
|----------------|-----|
| Черновая       | 3   |
| Предчистовая   | 2   |
| Чистовая       | 1   |

## Примеры использования

### Пример 1: Простое обновление

```typescript
import { updateFinishingMaterial } from '@/services/visaryCrud';

async function updateSiteFinishing(siteId: number) {
  try {
    const updated = await updateFinishingMaterial(siteId, 'Черновая');
    console.log('✓ Успешно обновлено:', updated.FinishingMaterial);
  } catch (error) {
    console.error('✗ Ошибка:', error.message);
  }
}
```

### Пример 2: Обновление из Excel данных

```typescript
import { updateFinishingMaterial } from '@/services/visaryCrud';

async function updateFromExcel(siteId: number, excelRow: any) {
  const finishingType = excelRow['Тип отделки'];
  
  if (!finishingType) {
    console.warn('Тип отделки не указан в Excel');
    return;
  }
  
  try {
    await updateFinishingMaterial(siteId, finishingType);
    console.log(`✓ Объект ${siteId}: тип отделки обновлён на "${finishingType}"`);
  } catch (error) {
    console.error(`✗ Объект ${siteId}: ошибка обновления -`, error.message);
  }
}
```

### Пример 3: Пакетное обновление

```typescript
import { updateFinishingMaterial } from '@/services/visaryCrud';

async function batchUpdate(sites: Array<{ id: number; finishingType: string }>) {
  const results = {
    success: 0,
    failed: 0,
    errors: [] as string[],
  };
  
  for (const site of sites) {
    try {
      await updateFinishingMaterial(site.id, site.finishingType);
      results.success++;
    } catch (error) {
      results.failed++;
      results.errors.push(`Объект ${site.id}: ${error.message}`);
    }
  }
  
  console.log(`✓ Успешно: ${results.success}, ✗ Ошибок: ${results.failed}`);
  if (results.errors.length > 0) {
    console.error('Ошибки:', results.errors);
  }
  
  return results;
}
```

## Обработка ошибок

### Типы ошибок

```typescript
import { updateFinishingMaterial } from '@/services/visaryCrud';
import { VisaryApiError, VisaryAuthError } from '@/services/visaryApi';

try {
  await updateFinishingMaterial(7791, 'Неизвестный');
} catch (error) {
  if (error instanceof VisaryAuthError) {
    // Токен истёк
    alert('Токен Visary истёк. Обновите .env.local');
  } else if (error instanceof VisaryApiError) {
    // Ошибка API (404, 409, 500)
    alert(`Ошибка API: ${error.status} - ${error.message}`);
  } else if (error instanceof Error) {
    // Тип отделки не найден
    alert(`Ошибка: ${error.message}`);
  }
}
```

### Валидация типа отделки

```typescript
import { getFinishingMaterialId } from '@/services/visaryCrud';

function validateFinishingType(type: string): boolean {
  return getFinishingMaterialId(type) !== null;
}

// Использование
const type = 'Черновая';
if (validateFinishingType(type)) {
  await updateFinishingMaterial(7791, type);
} else {
  console.error(`Неизвестный тип отделки: "${type}"`);
}
```

## Логирование

Все операции автоматически логируются в консоль:

```
[updateFinishingMaterial] Обновление объекта 7791: FinishingMaterial="Черновая"
[VisaryCRUD] → GET /crud/constructionsite/7791
[VisaryCRUD] ← GET /crud/constructionsite/7791 | RowVersion=3364373
[VisaryCRUD] → PATCH /crud/constructionsite/7791?forceUpdate=true
[VisaryCRUD] ← PATCH /crud/constructionsite/7791 | успешно обновлено
[updateFinishingMaterial] ✓ Объект 7791 обновлён: FinishingMaterial.ID=3
```

## Тестирование

### Запуск unit-тестов

```bash
cd KiloImportService.Web
npx tsx src/services/__tests__/visaryCrud.test.ts
```

### Ручное тестирование

Откройте DevTools Console в браузере и выполните:

```javascript
// Импортируем (если используется в компоненте)
import { updateFinishingMaterial } from '@/services/visaryCrud';

// Тест
await updateFinishingMaterial(7791, 'Черновая');
```

## Требования

- ✅ Bearer-токен в `.env.local` (`VITE_VISARY_API_TOKEN`)
- ✅ ID объекта строительства
- ✅ Корректное название типа отделки

## Troubleshooting

### Ошибка: "Тип отделки не найден в справочнике"

**Причина**: Неправильное название типа отделки

**Решение**: Используйте одно из: "Черновая", "Предчистовая", "Чистовая"

### Ошибка: "Bearer-токен истёк"

**Причина**: Токен в `.env.local` устарел

**Решение**: 
1. Получите новый токен из Visary
2. Обновите `VITE_VISARY_API_TOKEN` в `.env.local`
3. Перезапустите dev-сервер

### Ошибка: "409 Conflict"

**Причина**: `RowVersion` устарел (объект был изменён другим пользователем)

**Решение**: Функция `updateFinishingMaterial` автоматически получает актуальный `RowVersion`, поэтому эта ошибка возникает редко. Если возникла - повторите запрос.

## См. также

- 📚 [Полная документация](./doc_project/22-update-finishing-material.md)
- 🏗️ [Получение объектов строительства](./doc_project/21-sites-by-project.md)
- 🔌 [Интеграция с Visary API](./doc_project/08-visary-api-integration.md)
