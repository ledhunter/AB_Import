import { createListViewService } from '../createListViewService';
import type { ListViewMapper, ListViewQuery } from '../types';
import type { ConstructionSiteRaw, SiteItem } from '../../../types/listView';

/**
 * Колонки, запрашиваемые у Visary для списка объектов строительства.
 *
 * ⚠️ Список предварительный — уточнить при первом реальном запросе.
 * Если Visary вернёт другие имена полей, обновить и `ConstructionSiteRaw`,
 * и эту константу одновременно (см. doc_project/08-visary-api-integration.md,
 * раздел про `Data` vs `Items`).
 */
export const SITE_COLUMNS = [
  'ID',
  'Title',
  'ConstructionPermissionNumber',
  'ConstructionProjectNumber',
  'StageNumber',
  'ConstructionProjectID',
];

/**
 * Маппинг сырой строки Visary в UI-тип `SiteItem`.
 * Все строковые поля имеют fallback через `||` (пустые строки заменяются на дефолт).
 */
export const toSiteItem: ListViewMapper<ConstructionSiteRaw, SiteItem> = (raw) => ({
  id: raw.ID,
  title: raw.Title || `Объект #${raw.ID}`,
  constructionPermissionNumber: raw.ConstructionPermissionNumber || '',
  constructionProjectNumber: raw.ConstructionProjectNumber || '',
  stageNumber: raw.StageNumber || '',
  raw,
});

/**
 * Сервис для получения списка объектов строительства из Visary ListView API.
 */
export const sitesService = createListViewService<ConstructionSiteRaw, SiteItem>({
  mnemonic: 'constructionsite',
  columns: SITE_COLUMNS,
  defaultSort: '[{"selector":"ID","desc":true}]',
  toItem: toSiteItem,
  logTag: '[sites]',
});

/**
 * Helper: построить ListViewQuery с фильтром «объекты данного проекта».
 *
 * ⚠️ Точный формат `ExtraFilter` зависит от Visary — здесь dev-вариант на
 * базе DevExtreme-синтаксиса (`[["ConstructionProjectID","=",projectId]]`).
 * Реальное значение подтвердить логами + тестом на реальном API.
 */
export function buildSitesQueryByProject(
  projectId: number,
  query: Omit<ListViewQuery, 'extraFilter' | 'associatedId'> = {},
): ListViewQuery {
  return {
    ...query,
    associatedId: projectId,
    extraFilter: `[["ConstructionProjectID","=",${projectId}]]`,
  };
}
