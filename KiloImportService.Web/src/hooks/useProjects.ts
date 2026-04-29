import { projectsService } from '../services/listView/entities/projects';
import { useListView } from './useListView';
import type { UseListViewState } from './useListView';
import type { ProjectItem } from '../types/listView';

/** @deprecated Прямой импорт из useListView — оставлено для обратной совместимости. */
export type ProjectsStatus = 'idle' | 'loading' | 'success' | 'error';

/**
 * Хук lazy-загрузки проектов строительства из Visary API.
 *
 * Тонкая обёртка над generic-`useListView(projectsService)`:
 *   - Запрос НЕ происходит автоматически при монтировании.
 *   - Вызови `load()` (например, в `onOpen` у Select), чтобы инициировать запрос.
 *   - Поведение и контракт совпадают с предыдущей версией хука — ImportForm не трогаем.
 *
 * См. doc_project/09-lazy-loaded-select.md.
 */
export function useProjects(): UseListViewState<ProjectItem> {
  return useListView(projectsService, { logTag: '[useProjects]' });
}
