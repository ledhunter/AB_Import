import { useCallback, useEffect, useRef, useState } from 'react';
import type { ListViewQuery, ListViewService } from '../services/listView/types';

/**
 * Состояния lazy-загрузки.
 * Подробное описание машины — в doc_project/09-lazy-loaded-select.md.
 */
export type ListViewStatus = 'idle' | 'loading' | 'success' | 'error';

export interface UseListViewState<TItem> {
  data: TItem[];
  status: ListViewStatus;
  error: string | null;
  totalCount: number;
  /**
   * Загружает (или перезагружает) список из Visary API.
   * Идемпотентно: повторный вызов во время `loading` или после `success` игнорируется.
   * Используй `refetch()` для принудительной перезагрузки.
   */
  load: () => void;
  /**
   * Принудительный перезапрос — даже если уже success.
   */
  refetch: () => void;
}

export interface UseListViewOptions {
  /**
   * Параметры запроса (pageSkip/pageSize/searchString/extraFilter и т.п.).
   * При изменении референса — кэш данных сбрасывается, в `idle` (для нового
   * первого `load()` пользователем). Это позволяет, например, при смене
   * выбранного проекта — заставить зависимый Select заново загрузить данные.
   *
   * ⚠️ Стабилизируй ссылку через `useMemo`, иначе reset будет на каждый рендер.
   */
  query?: ListViewQuery;
  /**
   * Тег для логов потребителя — обычно имя хука-обёртки, например `[useProjects]`.
   * Если не передан, используется `service.logTag`.
   */
  logTag?: string;
}

/**
 * Generic-хук для lazy-загрузки данных из Visary ListView.
 *
 * Универсален: работает с любым `ListViewService<TItem>` из библиотеки
 * `services/listView/`. Логика lazy-load + AbortController + статус-машина
 * вынесена сюда; per-entity хуки (useProjects, useSites, ...) — тонкие обёртки.
 *
 * См. doc_project/09-lazy-loaded-select.md.
 */
export function useListView<TItem>(
  service: ListViewService<TItem>,
  options: UseListViewOptions = {},
): UseListViewState<TItem> {
  const { query, logTag = service.logTag } = options;

  const [data, setData] = useState<TItem[]>([]);
  const [status, setStatus] = useState<ListViewStatus>('idle');
  const [error, setError] = useState<string | null>(null);
  const [totalCount, setTotalCount] = useState(0);

  const inFlightRef = useRef<AbortController | null>(null);
  // Актуальный query храним в ref — load/refetch читают его из ref'а, чтобы
  // самим коллбэкам не пересоздаваться на каждое изменение query.
  // Обновление ref'а — строго в useEffect (правило React: refs не пишутся при рендере).
  const queryRef = useRef<ListViewQuery | undefined>(query);

  // При смене query (по ссылке) — сбрасываем кэш в idle, отменяем активный запрос.
  // Это нужно для зависимых Select-ов: смена projectId → sites должны перезагрузиться.
  useEffect(() => {
    // Первый коммит: ref уже инициализирован значением query — ничего не делаем.
    if (queryRef.current === query) {
      return;
    }
    queryRef.current = query;
    if (inFlightRef.current) {
      console.info(`${logTag} query изменился — отменяю активный запрос`);
      inFlightRef.current.abort();
      inFlightRef.current = null;
    }
    console.info(`${logTag} query изменился → сброс в idle`);
    setData([]);
    setTotalCount(0);
    setError(null);
    setStatus('idle');
  }, [query, logTag]);

  const run = useCallback(() => {
    if (inFlightRef.current) {
      console.warn(`${logTag} прерываю предыдущий запрос`);
      inFlightRef.current.abort();
    }
    const controller = new AbortController();
    inFlightRef.current = controller;

    console.info(`${logTag} status: → loading`);
    setStatus('loading');
    setError(null);

    service
      .fetch({ ...queryRef.current, signal: controller.signal })
      .then((res) => {
        if (controller.signal.aborted) return;
        console.info(
          `${logTag} ✓ status: loading → success | получено ${res.items.length} из ${res.totalCount}`,
        );
        if (res.items.length > 0) {
          console.info(`${logTag} первые 3 элемента:`, res.items.slice(0, 3));
        }
        setData(res.items);
        setTotalCount(res.totalCount);
        setStatus('success');
        inFlightRef.current = null;
      })
      .catch((err: unknown) => {
        if (err instanceof Error && err.name === 'AbortError') return;
        const message = err instanceof Error ? err.message : String(err);
        console.error(`${logTag} ✗ status: loading → error |`, message);
        setError(message);
        setStatus('error');
        inFlightRef.current = null;
      });
  }, [service, logTag]);

  const load = useCallback(() => {
    if (status === 'loading') {
      console.info(`${logTag} load() пропущен — запрос уже в полёте`);
      return;
    }
    if (status === 'success') {
      console.info(
        `${logTag} load() пропущен — данные уже загружены (${data.length}). Используй refetch() для перезагрузки.`,
      );
      return;
    }
    console.info(`${logTag} load() вызван (текущий status: ${status})`);
    run();
  }, [status, data.length, run, logTag]);

  const refetch = useCallback(() => {
    console.info(`${logTag} refetch() вызван — принудительная перезагрузка`);
    run();
  }, [run, logTag]);

  // Отмена при размонтировании.
  useEffect(() => {
    return () => {
      if (inFlightRef.current) {
        inFlightRef.current.abort();
        inFlightRef.current = null;
      }
    };
  }, []);

  return { data, status, error, totalCount, load, refetch };
}
