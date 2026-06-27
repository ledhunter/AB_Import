# 🚀 Быстрый старт: API объектов строительства

## 📋 Что добавлено

Реализован метод получения объектов строительства (construction sites), отфильтрованных по выбранному проекту.

### Изменённые файлы

1. **`src/services/listView/types.ts`**
   - Добавлен интерфейс `AssociationFilter`
   - Добавлено поле `associationFilter` в `ListViewQuery`
   - Добавлено поле `pathSuffix` в `ListViewServiceConfig`

2. **`src/services/listView/createListViewService.ts`**
   - Обновлён `ListViewRequestBody` для поддержки `AssociationFilter`
   - Обновлён `buildListViewRequestBody` для передачи `AssociationFilter`
   - Обновлён `createListViewService` для поддержки `pathSuffix`

3. **`src/types/listView.ts`**
   - Обновлён `ConstructionSiteRaw` с полным списком колонок из API
   - Обновлён `SiteItem` с актуальными полями

4. **`src/services/listView/entities/sites.ts`**
   - Обновлён `SITE_COLUMNS` с полным списком колонок
   - Обновлён `toSiteItem` маппер
   - Добавлен `pathSuffix: '/onetomany/Project'` в конфигурацию сервиса
   - Обновлён `buildSitesQueryByProject` для использования `AssociationFilter`

5. **`doc_project/21-sites-by-project.md`**
   - Полная документация по новому методу

6. **`doc_project/README.md`**
   - Добавлена ссылка на новый документ

---

## ⚙️ Настройка

### 1. Создай `.env.local` (если ещё не создан)

```bash
cd KiloImportService.Web
cp .env.example .env.local
```

### 2. Добавь токен в `.env.local`

```env
VITE_VISARY_API_URL=https://isup-alfa-test.k8s.npc.ba
VITE_VISARY_API_TOKEN=<ВАШ_JWT_ТОКЕН>
```

⚠️ **Важно**: токен без префикса `"Bearer "` — он добавляется автоматически.

### 3. Перезапусти dev-сервер

```bash
npm run dev
```

---

## 🎯 Использование в коде

### Пример 1: Базовое использование

```tsx
import { useSites } from '../hooks/useSites';

function MyComponent() {
  const [projectId, setProjectId] = useState<number | null>(null);
  const sites = useSites(projectId);

  useEffect(() => {
    if (projectId !== null) {
      sites.load();
    }
  }, [projectId]);

  if (sites.status === 'loading') return <div>Загрузка...</div>;
  if (sites.status === 'error') return <div>Ошибка: {sites.error}</div>;

  return (
    <ul>
      {sites.data.map(site => (
        <li key={site.id}>
          {site.title} - {site.address}
        </li>
      ))}
    </ul>
  );
}
```

### Пример 2: Select с объектами строительства

```tsx
import { Select } from '@alfalab/core-components/select';
import { useSites } from '../hooks/useSites';

function SiteSelector({ projectId, onSiteChange }) {
  const sites = useSites(projectId);

  useEffect(() => {
    if (projectId !== null) {
      sites.load();
    }
  }, [projectId]);

  return (
    <Select
      label="Объект строительства"
      disabled={projectId === null || sites.status === 'loading'}
      options={sites.data.map(site => ({
        key: String(site.id),
        content: `${site.title} (${site.address})`,
      }))}
      onChange={({ selected }) => {
        if (selected) {
          onSiteChange(Number(selected.key));
        }
      }}
    />
  );
}
```

### Пример 3: Прямой вызов сервиса

```ts
import { sitesService, buildSitesQueryByProject } from '../services/listView/entities/sites';

async function loadSitesForProject(projectId: number) {
  const query = buildSitesQueryByProject(projectId, {
    pageSize: 100,
  });

  const result = await sitesService.fetch(query);
  console.log(`Загружено ${result.items.length} из ${result.totalCount} объектов`);
  return result.items;
}
```

---

## 🔍 Проверка работы

### 1. Через DevTools

1. Открой DevTools → Network
2. Выбери проект в форме
3. Найди запрос к `/api/visary/listview/constructionsite/onetomany/Project`
4. Проверь тело запроса:

```json
{
  "Mnemonic": "constructionsite",
  "AssociationFilter": {
    "AssociatedId": 123,  // ID выбранного проекта
    "Filters": null
  }
}
```

### 2. Через curl

```bash
curl --location 'https://isup-alfa-test.k8s.npc.ba/api/visary/listview/constructionsite/onetomany/Project' \
--header 'Content-Type: application/json' \
--header 'Authorization: Bearer <ВАШ_ТОКЕН>' \
--data '{
    "Mnemonic": "constructionsite",
    "PageSkip": 0,
    "PageSize": 10,
    "Columns": ["ID", "Title", "Address", "Type"],
    "Sorts": "[{\"selector\":\"ID\",\"desc\":false}]",
    "Hidden": false,
    "ExtraFilter": null,
    "SearchString": "",
    "AssociationFilter": {
        "AssociatedId": 123,
        "Filters": null
    }
}'
```

---

## 📊 Доступные поля

### SiteItem (UI-модель)

```ts
{
  id: number;
  title: string;
  address: string;
  constructionProjectNumber: string;
  type: string;
  totalArea: number | null;
  raw?: ConstructionSiteRaw;  // Полные данные из API
}
```

### ConstructionSiteRaw (полные данные из API)

- `ID`, `Date`, `Project`, `Title`, `Location`
- `ConstructionProjectNumber`, `Address`, `Type`
- `EstateClass`, `BuildingMaterial`, `FinishingMaterial`
- `TotalArea`, `StartDate`, `FinishDate`, `MonthDuration`
- `TempOfConstruction`, `ClaimedCost`, `AreaCost`
- `RiskFund`, `Borrower`, `ComplexID`, `Town`
- `Comment`, `RowVersion`

---

## ❓ Частые вопросы

### Q: Почему Select пустой?

**A:** Проверь:
1. Токен задан в `.env.local`
2. Dev-сервер перезапущен после добавления токена
3. `projectId !== null` перед вызовом `sites.load()`
4. В DevTools нет ошибок 401/403

### Q: Как добавить больше полей в UI?

**A:** Обнови `SiteItem` в `types/listView.ts` и маппер `toSiteItem` в `services/listView/entities/sites.ts`:

```ts
export interface SiteItem {
  id: number;
  title: string;
  address: string;
  // Добавь новые поля
  totalArea: number | null;
  startDate: string;
  raw?: ConstructionSiteRaw;
}

export const toSiteItem = (raw) => ({
  id: raw.ID,
  title: raw.Title || `Объект #${raw.ID}`,
  address: raw.Address || '',
  totalArea: raw.TotalArea ?? null,
  startDate: raw.StartDate || '',  // Новое поле
  raw,
});
```

### Q: Как изменить сортировку?

**A:** Передай `sorts` в `buildSitesQueryByProject`:

```ts
const query = buildSitesQueryByProject(projectId, {
  sorts: '[{"selector":"Title","desc":false}]',
});
```

---

## 📚 Дополнительная документация

- **Полная документация**: `doc_project/21-sites-by-project.md`
- **Архитектура ListView**: `doc_project/10-listview-library.md`
- **Интеграция с Visary**: `doc_project/08-visary-api-integration.md`
- **Lazy-load паттерн**: `doc_project/09-lazy-loaded-select.md`
