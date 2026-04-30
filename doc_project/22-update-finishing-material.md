# Обновление типа отделки объекта строительства

## Описание

Модуль для обновления поля `FinishingMaterial` (Тип отделки) объекта строительства через Visary CRUD API.

## Архитектура

### Файлы

- **`src/services/visaryCrud.ts`** - сервис для работы с Visary CRUD API
- **`src/services/visaryApi.ts`** - базовые HTTP методы (GET, POST, PATCH)
- **`src/services/__tests__/visaryCrud.test.ts`** - unit-тесты

### API Endpoints

#### 1. Получение объекта строительства

```
GET /api/visary/crud/constructionsite/{id}
```

**Response:**
```json
{
  "ID": 7791,
  "RowVersion": 3364373,
  "Title": "Тест ФМ - Опус 234523452345 1",
  "FinishingMaterial": {
    "ID": 3,
    "Title": "Черновая"
  },
  ...
}
```

#### 2. Обновление объекта строительства

```
PATCH /api/visary/crud/constructionsite/{id}?forceUpdate=true
```

**Request Body:**
```json
{
  "ID": 7791,
  "RowVersion": 3364373,
  "FinishingMaterial": {
    "ID": 3
  }
}
```

**Response:** обновлённая сущность с новым `RowVersion`

## Справочник "Тип отделки"

| ID | Название       |
|----|----------------|
| 3  | Черновая       |
| 2  | Предчистовая   |
| 1  | Чистовая       |

## Использование

### Базовые функции

```typescript
import {
  getConstructionSite,
  updateConstructionSite,
  getFinishingMaterialId,
  updateFinishingMaterial,
} from '@/services/visaryCrud';

// 1. Получить ID типа отделки по названию
const finishingMaterialId = getFinishingMaterialId('Черновая');
// => 3

// 2. Получить объект строительства
const site = await getConstructionSite(7791);
console.log(site.RowVersion); // => 3364373

// 3. Обновить тип отделки (низкоуровневый способ)
const payload = {
  ID: 7791,
  RowVersion: site.RowVersion,
  FinishingMaterial: { ID: 3 },
};
const updated = await updateConstructionSite(7791, payload);

// 4. Обновить тип отделки (высокоуровневый способ)
const updated = await updateFinishingMaterial(7791, 'Черновая');
```

### Высокоуровневая функция

Функция `updateFinishingMaterial` автоматически:
1. Получает текущий `RowVersion` объекта
2. Преобразует название типа отделки в ID
3. Обновляет объект

```typescript
import { updateFinishingMaterial } from '@/services/visaryCrud';

try {
  const updated = await updateFinishingMaterial(7791, 'Черновая');
  console.log('✓ Тип отделки обновлён:', updated.FinishingMaterial);
} catch (error) {
  console.error('Ошибка обновления:', error.message);
}
```

### Обработка ошибок

```typescript
import { updateFinishingMaterial } from '@/services/visaryCrud';
import { VisaryApiError, VisaryAuthError } from '@/services/visaryApi';

try {
  await updateFinishingMaterial(7791, 'Неизвестный тип');
} catch (error) {
  if (error instanceof VisaryAuthError) {
    // Токен истёк - нужно обновить .env.local
    console.error('Токен истёк:', error.message);
  } else if (error instanceof VisaryApiError) {
    // Ошибка API (404, 500, и т.д.)
    console.error(`API ошибка ${error.status}:`, error.message);
  } else if (error instanceof Error) {
    // Тип отделки не найден в справочнике
    console.error('Ошибка:', error.message);
  }
}
```

## Интеграция с процессом импорта

Для интеграции обновления типа отделки в процесс импорта:

1. Прочитать значение "Тип отделки" из Excel файла
2. Вызвать `updateFinishingMaterial` для каждого объекта строительства

```typescript
import { updateFinishingMaterial } from '@/services/visaryCrud';

// Пример: обновление после импорта
async function afterImport(siteId: number, excelData: any) {
  const finishingMaterialTitle = excelData['Тип отделки'];
  
  if (finishingMaterialTitle) {
    try {
      await updateFinishingMaterial(siteId, finishingMaterialTitle);
      console.log(`✓ Тип отделки обновлён для объекта ${siteId}`);
    } catch (error) {
      console.error(`✗ Ошибка обновления объекта ${siteId}:`, error);
    }
  }
}
```

## Тестирование

### Запуск unit-тестов

```bash
npx tsx src/services/__tests__/visaryCrud.test.ts
```

### Ручное тестирование через DevTools Console

```javascript
// Импортируем функцию (если используется в компоненте)
import { updateFinishingMaterial } from '@/services/visaryCrud';

// Тестовый запрос
await updateFinishingMaterial(7791, 'Черновая');
```

## Логирование

Все операции логируются в консоль:

```
[VisaryCRUD] → GET /crud/constructionsite/7791
[VisaryCRUD] ← GET /crud/constructionsite/7791 | RowVersion=3364373
[updateFinishingMaterial] Обновление объекта 7791: FinishingMaterial="Черновая"
[VisaryCRUD] → PATCH /crud/constructionsite/7791?forceUpdate=true
[VisaryCRUD] ← PATCH /crud/constructionsite/7791 | успешно обновлено
[updateFinishingMaterial] ✓ Объект 7791 обновлён: FinishingMaterial.ID=3
```

## Оптимистичная блокировка

Visary API использует оптимистичную блокировку через поле `RowVersion`:

- При каждом обновлении нужно передавать актуальный `RowVersion`
- После обновления `RowVersion` увеличивается
- Если `RowVersion` устарел - API вернёт ошибку 409 Conflict

Функция `updateFinishingMaterial` автоматически получает актуальный `RowVersion` перед обновлением.

## Безопасность

- Bearer-токен берётся из `VITE_VISARY_API_TOKEN` (.env.local)
- Токен передаётся в заголовке `Authorization: Bearer {token}`
- При 401/403 выбрасывается `VisaryAuthError`

## См. также

- [21-sites-by-project.md](./21-sites-by-project.md) - получение объектов строительства по проекту
- [18-projects-cache.md](./18-projects-cache.md) - кэш проектов
