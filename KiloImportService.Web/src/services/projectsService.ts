/**
 * @deprecated Используй новую библиотеку: `services/listView/entities/projects.ts`
 *             либо хук `hooks/useProjects.ts`. Этот файл сохранён только ради
 *             обратной совместимости (старые тесты + импорты useProjects).
 *
 * См. doc_project/plan-listview-library.md.
 */
import { parseListViewResponse } from './listView/parseListViewResponse';
import { projectsService, toProjectItem } from './listView/entities/projects';
import type { ListViewResponseRaw } from './listView/types';
import type { ConstructionProjectRaw, ProjectItem } from '../types/listView';

export { toProjectItem };
export { PROJECT_COLUMNS } from './listView/entities/projects';

interface FetchProjectsOptions {
  pageSkip?: number;
  pageSize?: number;
  searchString?: string;
  signal?: AbortSignal;
}

/**
 * @deprecated используй `projectsService.fetch(...)` из `services/listView`.
 */
export async function fetchProjects(
  options: FetchProjectsOptions = {},
): Promise<{ items: ProjectItem[]; totalCount: number }> {
  return projectsService.fetch(options);
}

/**
 * @deprecated используй `parseListViewResponse(raw, toProjectItem)` напрямую.
 */
export function parseProjectsResponse(
  raw: ListViewResponseRaw<ConstructionProjectRaw>,
): { items: ProjectItem[]; totalCount: number } {
  return parseListViewResponse(raw, toProjectItem);
}
