import { useMemo } from 'react';
import {
  buildSitesQueryByProject,
  sitesService,
} from '../services/listView/entities/sites';
import { useListView } from './useListView';
import type { UseListViewState } from './useListView';
import type { SiteItem } from '../types/listView';

/**
 * Хук lazy-загрузки объектов строительства, отфильтрованных по проекту.
 *
 * @param projectId — id выбранного проекта; если `null`, хук возвращает `idle`
 *   (запрос не пойдёт даже при `load()`). Это удобно для зависимых Select-ов:
 *   Select «Объект» disabled, пока не выбран проект.
 *
 * При смене `projectId` кэш данных сбрасывается → следующий `load()` дёрнет API
 * заново уже с новым фильтром.
 */
export function useSites(projectId: number | null): UseListViewState<SiteItem> {
  // Стабилизируем ссылку на query — useListView сравнивает по ссылке, чтобы
  // понять, нужно ли сбросить кэш. См. реализацию useListView.
  const query = useMemo(
    () => (projectId !== null ? buildSitesQueryByProject(projectId) : undefined),
    [projectId],
  );

  const state = useListView(sitesService, {
    query,
    logTag: '[useSites]',
  });

  // Если проект ещё не выбран — load() будет no-op (нет смысла запрашивать всё).
  if (projectId === null) {
    return {
      ...state,
      load: () => console.info('[useSites] load() пропущен — projectId не выбран'),
    };
  }
  return state;
}
