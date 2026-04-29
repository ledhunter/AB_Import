import { createListViewService } from '../createListViewService';
import type { ListViewMapper } from '../types';
import type { ConstructionProjectRaw, ProjectItem } from '../../../types/listView';

/**
 * Колонки, которые запрашиваем у Visary для списка проектов строительства.
 * Должны совпадать с полями `ConstructionProjectRaw` (PascalCase).
 */
export const PROJECT_COLUMNS = [
  'ID',
  'IdentifierKK',
  'IdentifierZPLM',
  'Title',
  'Type',
  'Phase',
  'Region',
  'Town',
  'Developer',
  'ProjectManagment',
  'Sponsor',
  'ProjectPeriod',
  'RowVersion',
];

/**
 * Маппинг сырой строки Visary в UI-тип `ProjectItem`.
 *
 * ⚠️ Используем `||`, а не `??`: backend может вернуть пустую строку
 * (`raw.Title === ''`), в этом случае нужен fallback. См. doc_project/08-visary-api-integration.md.
 */
export const toProjectItem: ListViewMapper<ConstructionProjectRaw, ProjectItem> = (raw) => ({
  id: raw.ID,
  title: raw.Title || `Проект #${raw.ID}`,
  code: raw.IdentifierKK || raw.IdentifierZPLM || `ID-${raw.ID}`,
  raw,
});

/**
 * Сервис для получения списка проектов строительства из Visary ListView API.
 */
export const projectsService = createListViewService<ConstructionProjectRaw, ProjectItem>({
  mnemonic: 'constructionproject',
  columns: PROJECT_COLUMNS,
  defaultSort: '[{"selector":"ID","desc":true}]',
  toItem: toProjectItem,
  logTag: '[projects]',
});
