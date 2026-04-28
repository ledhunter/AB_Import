import { visaryPost } from './visaryApi';
import type {
  ConstructionProjectRaw,
  ListViewRequest,
  ListViewResponse,
  ProjectItem,
} from '../types/listView';

const PROJECT_COLUMNS = [
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

interface FetchProjectsOptions {
  pageSkip?: number;
  pageSize?: number;
  searchString?: string;
  signal?: AbortSignal;
}

/**
 * Запрашивает список проектов строительства из Visary ListView API.
 * @returns Нормализованный список + общее число записей.
 */
export async function fetchProjects(
  options: FetchProjectsOptions = {},
): Promise<{ items: ProjectItem[]; totalCount: number }> {
  const { pageSkip = 0, pageSize = 50, searchString = '', signal } = options;

  const request: ListViewRequest = {
    Mnemonic: 'constructionproject',
    PageSkip: pageSkip,
    PageSize: pageSize,
    Columns: PROJECT_COLUMNS,
    Sorts: '[{"selector":"ID","desc":true}]',
    Hidden: false,
    ExtraFilter: null,
    SearchString: searchString,
    AssociatedID: null,
  };

  const raw = await visaryPost<ListViewResponse<ConstructionProjectRaw>>(
    '/listview/constructionproject',
    request,
    { signal },
  );
  return parseProjectsResponse(raw);
}

/**
 * Извлекает массив строк и общее количество из ответа Visary.
 * Поддерживает разные ключи (`Data`/`Items`/`items`, `Total`/`TotalCount`/`totalCount`).
 */
export function parseProjectsResponse(
  raw: ListViewResponse<ConstructionProjectRaw>,
): { items: ProjectItem[]; totalCount: number } {
  const rows = raw.Data ?? raw.Items ?? raw.items ?? [];
  const items = rows.map(toProjectItem);
  const totalCount = raw.Total ?? raw.TotalCount ?? raw.totalCount ?? items.length;
  return { items, totalCount };
}

/**
 * Маппинг сырой строки Visary в UI-тип `ProjectItem`.
 */
export function toProjectItem(raw: ConstructionProjectRaw): ProjectItem {
  return {
    id: raw.ID,
    title: raw.Title || `Проект #${raw.ID}`,
    code: raw.IdentifierKK || raw.IdentifierZPLM || `ID-${raw.ID}`,
    raw,
  };
}
