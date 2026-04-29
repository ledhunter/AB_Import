import type { ListViewMapper, ListViewResponseRaw, ListViewResult } from './types';

/**
 * Извлекает массив строк и общее количество из ответа Visary ListView API.
 *
 * Поддерживает три варианта ключей в порядке приоритета:
 *   1. `Data` / `Total`            — реальный формат Visary (зафиксирован эмпирически)
 *   2. `Items` / `TotalCount`      — fallback на случай PascalCase других эндпоинтов
 *   3. `items` / `totalCount`      — fallback на случай camelCase-сериализации
 *
 * ⚠️ Историческая справка: первая реализация ожидала `Items`/`TotalCount`,
 * UI получал HTTP 200, но видел «0 из 0 проектов», потому что Visary вернул
 * `Data`/`Total`. Этот баг был пойман через логирование ответа.
 * См. doc_project/08-visary-api-integration.md.
 */
export function parseListViewResponse<TRaw, TItem>(
  raw: ListViewResponseRaw<TRaw>,
  toItem: ListViewMapper<TRaw, TItem>,
): ListViewResult<TItem> {
  const rows = raw.Data ?? raw.Items ?? raw.items ?? [];
  const items = rows.map(toItem);
  const totalCount = raw.Total ?? raw.TotalCount ?? raw.totalCount ?? items.length;
  return { items, totalCount };
}
