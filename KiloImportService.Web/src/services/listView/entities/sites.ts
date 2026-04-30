import { createListViewService } from '../createListViewService';
import type { ListViewMapper, ListViewQuery } from '../types';
import type { ConstructionSiteRaw, SiteItem } from '../../../types/listView';

/**
 * Колонки, запрашиваемые у Visary для списка объектов строительства.
 * Список соответствует API Visary ListView.
 */
export const SITE_COLUMNS = [
  'ID',
  'Date',
  'Project',
  'Title',
  'Location',
  'ConstructionProjectNumber',
  'Address',
  'Type',
  'EstateClass',
  'BuildingMaterial',
  'FinishingMaterial',
  'TotalArea',
  'StartDate',
  'FinishDate',
  'MonthDuration',
  'TempOfConstruction',
  'ClaimedCost',
  'AreaCost',
  'RiskFund',
  'Borrower',
  'ComplexID',
  'Town',
  'Comment',
  'RowVersion',
];

/**
 * Маппинг сырой строки Visary в UI-тип `SiteItem`.
 * Все строковые поля имеют fallback через `||` (пустые строки заменяются на дефолт).
 */
export const toSiteItem: ListViewMapper<ConstructionSiteRaw, SiteItem> = (raw) => ({
  id: raw.ID,
  title: raw.Title || `Объект #${raw.ID}`,
  address: raw.Address || '',
  constructionProjectNumber: raw.ConstructionProjectNumber || '',
  type: raw.Type || '',
  totalArea: raw.TotalArea ?? null,
  raw,
});

/**
 * Сервис для получения списка объектов строительства из Visary ListView API.
 * Использует специальный эндпоинт /onetomany/Project для фильтрации по проекту.
 */
export const sitesService = createListViewService<ConstructionSiteRaw, SiteItem>({
  mnemonic: 'constructionsite',
  pathSuffix: '/onetomany/Project',
  columns: SITE_COLUMNS,
  defaultSort: '[{"selector":"ID","desc":false}]',
  toItem: toSiteItem,
  logTag: '[sites]',
});

/**
 * Helper: построить ListViewQuery с фильтром «объекты данного проекта».
 * Использует AssociationFilter для фильтрации по связанному проекту.
 */
export function buildSitesQueryByProject(
  projectId: number,
  query: Omit<ListViewQuery, 'associationFilter'> = {},
): ListViewQuery {
  return {
    ...query,
    associationFilter: {
      AssociatedId: projectId,
      Filters: null,
    },
  };
}
