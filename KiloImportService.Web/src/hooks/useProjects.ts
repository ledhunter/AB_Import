import { useCallback, useRef, useState } from 'react';
import { fetchProjects } from '../services/projectsService';
import type { ProjectItem } from '../types/listView';

export type ProjectsStatus = 'idle' | 'loading' | 'success' | 'error';

interface UseProjectsState {
  data: ProjectItem[];
  status: ProjectsStatus;
  error: string | null;
  totalCount: number;
  /**
   * Загружает (или перезагружает) список проектов из Visary API.
   * Идемпотентно — повторный вызов во время уже идущего запроса игнорируется.
   */
  load: () => void;
  /**
   * Принудительный перезапрос (даже если уже success).
   */
  refetch: () => void;
}

/**
 * Хук для ленивой (по требованию) загрузки списка проектов.
 *
 * Запрос НЕ происходит автоматически при монтировании компонента.
 * Вызовите `load()` (например, в `onOpen` у Select), чтобы инициировать запрос.
 */
export function useProjects(): UseProjectsState {
  const [data, setData] = useState<ProjectItem[]>([]);
  const [status, setStatus] = useState<ProjectsStatus>('idle');
  const [error, setError] = useState<string | null>(null);
  const [totalCount, setTotalCount] = useState(0);
  const inFlightRef = useRef<AbortController | null>(null);

  const run = useCallback(() => {
    // Отменяем предыдущий запрос, если он в полёте
    if (inFlightRef.current) {
      console.warn('[useProjects] Прерываю предыдущий запрос');
      inFlightRef.current.abort();
    }
    const controller = new AbortController();
    inFlightRef.current = controller;

    console.info('[useProjects] status: idle/error → loading');
    setStatus('loading');
    setError(null);

    fetchProjects({ signal: controller.signal })
      .then((res) => {
        if (controller.signal.aborted) return;
        console.info(
          `[useProjects] ✓ status: loading → success | получено ${res.items.length} из ${res.totalCount} проектов`,
        );
        if (res.items.length > 0) {
          console.info('[useProjects] первые 3 проекта:', res.items.slice(0, 3));
        }
        setData(res.items);
        setTotalCount(res.totalCount);
        setStatus('success');
      })
      .catch((err: unknown) => {
        if (err instanceof Error && err.name === 'AbortError') return;
        const message = err instanceof Error ? err.message : String(err);
        console.error('[useProjects] ✗ status: loading → error |', message);
        setError(message);
        setStatus('error');
      });
  }, []);

  const load = useCallback(() => {
    if (status === 'loading') {
      console.info('[useProjects] load() пропущен — запрос уже в полёте');
      return;
    }
    if (status === 'success') {
      console.info(
        `[useProjects] load() пропущен — данные уже загружены (${data.length} проектов). Используй refetch() для перезагрузки.`,
      );
      return;
    }
    console.info(`[useProjects] load() вызван (текущий status: ${status})`);
    run();
  }, [status, data.length, run]);

  const refetch = useCallback(() => {
    console.info('[useProjects] refetch() вызван — принудительная перезагрузка');
    run();
  }, [run]);

  return { data, status, error, totalCount, load, refetch };
}
