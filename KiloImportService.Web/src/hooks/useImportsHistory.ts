/**
 * useImportsHistory — загружает список сессий импорта (история) через
 * `GET /api/imports` с фильтрами и пагинацией.
 *
 * Это пассивный список: SignalR-обновления здесь не подписываются, потому что
 * история — это «холодные» завершённые сессии. Если пользователь хочет видеть
 * актуальное состояние активного импорта — он переходит в детальное view
 * (`useImportSessionDetail`), где грузится через REST `getImportSession`.
 *
 * Обновление списка — только по явному `refresh()` или смене фильтров.
 */

import { useCallback, useEffect, useRef, useState } from 'react';
import { ImportsApiError, listImports } from '../services/importsService';
import { toUiSessionSummary } from '../services/importMappers';
import type { UiSessionSummary } from '../types/session';

export interface ImportsHistoryFilters {
  /** PascalCase backend-статус: `'Applied' | 'Failed' | ...`. */
  status?: string;
  /** id типа импорта: `'rooms' | 'finmodel' | ...`. */
  importTypeCode?: string;
  /** ID проекта Visary для фильтрации (undefined — «Все проекты»). */
  projectId?: number;
  skip?: number;
  take?: number;
}

export interface UseImportsHistoryState {
  items: UiSessionSummary[];
  total: number;
  loading: boolean;
  error: string | null;
  /** Текущее значение пагинации/фильтров (для отрисовки контролов). */
  query: Required<Pick<ImportsHistoryFilters, 'skip' | 'take'>> &
    Pick<ImportsHistoryFilters, 'status' | 'importTypeCode' | 'projectId'>;
  refresh: () => Promise<void>;
  setFilters: (next: ImportsHistoryFilters) => void;
}

const DEFAULT_TAKE = 50;

export function useImportsHistory(
  initial: ImportsHistoryFilters = {},
): UseImportsHistoryState {
  const [items, setItems] = useState<UiSessionSummary[]>([]);
  const [total, setTotal] = useState(0);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [query, setQuery] = useState({
    skip: initial.skip ?? 0,
    take: initial.take ?? DEFAULT_TAKE,
    status: initial.status,
    importTypeCode: initial.importTypeCode,
    projectId: initial.projectId,
  });

  const abortRef = useRef<AbortController | null>(null);

  const load = useCallback(
    async (q: typeof query) => {
      abortRef.current?.abort();
      const ctrl = new AbortController();
      abortRef.current = ctrl;

      setLoading(true);
      setError(null);
      try {
        const resp = await listImports({
          skip: q.skip,
          take: q.take,
          status: q.status,
          importTypeCode: q.importTypeCode,
          projectId: q.projectId,
          signal: ctrl.signal,
        });
        if (ctrl.signal.aborted) return;
        setItems(resp.items.map(toUiSessionSummary));
        setTotal(resp.pagination.total);
      } catch (err) {
        if (err instanceof DOMException && err.name === 'AbortError') return;
        const message =
          err instanceof ImportsApiError
            ? err.message
            : err instanceof Error
              ? err.message
              : String(err);
        setError(message);
      } finally {
        if (!ctrl.signal.aborted) setLoading(false);
      }
    },
    [],
  );

  // Загружаем при монтировании и при каждом изменении query.
  useEffect(() => {
    void load(query);
    return () => abortRef.current?.abort();
  }, [load, query]);

  const refresh = useCallback(() => load(query), [load, query]);

  const setFilters = useCallback((next: ImportsHistoryFilters) => {
    setQuery((prev) => ({
      skip: next.skip ?? 0, // при смене фильтра сбрасываем на 0
      take: next.take ?? prev.take,
      status: 'status' in next ? next.status : prev.status,
      importTypeCode:
        'importTypeCode' in next ? next.importTypeCode : prev.importTypeCode,
      projectId: 'projectId' in next ? next.projectId : prev.projectId,
    }));
  }, []);

  return { items, total, loading, error, query, refresh, setFilters };
}
