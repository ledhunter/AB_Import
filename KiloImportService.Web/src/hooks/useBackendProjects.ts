import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import {
  searchProjects,
  syncProjects,
  toProjectItem,
} from '../services/projectsBackendApi';
import type { ProjectItem } from '../types/listView';

export type ProjectsStatus = 'idle' | 'syncing' | 'loading' | 'success' | 'error';

export interface UseBackendProjectsState {
  /** Текущий список проектов под выбранный query (или первая страница). */
  data: ProjectItem[];
  /** Статус последней операции (sync или search). */
  status: ProjectsStatus;
  /** Текст ошибки последней операции, если status='error'. */
  error: string | null;
  /** True, если backend вернул результат через fallback в Visary. */
  fromFallback: boolean;
  /** Прогрет ли кэш (выполнен sync хотя бы раз в этой сессии). */
  isWarmed: boolean;
  /** Запустить sync (бэкенд тянет всё из Visary). Идемпотентно. */
  sync: () => void;
  /** Принудительно перезапустить поиск/первую страницу. */
  refetch: () => void;
}

interface UseBackendProjectsOptions {
  /** Поисковая подстрока (контролируется снаружи, например, ImportForm). */
  searchString: string;
  /** Дебаунс между нажатиями клавиш в миллисекундах. */
  debounceMs?: number;
  /** Лимит результатов. */
  limit?: number;
  /** Тег для логов. */
  logTag?: string;
}

/**
 * Хук поиска проектов через собственный backend (`/api/projects`).
 *
 * Поведение (важно: sync — НЕ обязателен для работы):
 *   1. На первом open Select вызывается `sync()` — но он сначала пробует прочитать
 *      первую страницу из локального кэша. Если в кэше уже есть данные — sync
 *      пропускается и помечается warmed=true.
 *   2. Если кэш пуст — backend синхронизируется с Visary (POST /sync).
 *   3. ⚠️ Если sync падает (Visary недоступен / 401), но локальный кэш всё-таки
 *      что-то вернул — мы НЕ блокируем UI ошибкой, а оставляем status='success'.
 *      Юзер увидит проекты из последней успешной синхронизации.
 *   4. На каждое изменение `searchString` (с дебаунсом) — GET /api/projects/search?q=...
 *      Backend ищет локально, при пустом результате делает fallback в Visary.
 *
 * См. doc_project/18-projects-cache.md.
 */
export function useBackendProjects(
  options: UseBackendProjectsOptions,
): UseBackendProjectsState {
  const { searchString, debounceMs = 300, limit = 50, logTag = '[useBackendProjects]' } = options;

  const [data, setData] = useState<ProjectItem[]>([]);
  const [status, setStatus] = useState<ProjectsStatus>('idle');
  const [error, setError] = useState<string | null>(null);
  const [fromFallback, setFromFallback] = useState(false);
  const [isWarmed, setIsWarmed] = useState(false);

  const inFlightRef = useRef<AbortController | null>(null);
  const isWarmedRef = useRef(false);

  // Latest-value pattern: храним актуальное searchString в ref, чтобы run() мог его читать.
  const searchRef = useRef(searchString);
  useEffect(() => {
    searchRef.current = searchString;
  }, [searchString]);

  const runSearch = useCallback(async () => {
    inFlightRef.current?.abort();
    const controller = new AbortController();
    inFlightRef.current = controller;

    const q = searchRef.current;
    console.info(`${logTag} search → loading q='${q}'`);
    setStatus('loading');
    setError(null);

    try {
      const res = await searchProjects(q, { limit, signal: controller.signal });
      if (controller.signal.aborted) return;
      setData(res.items.map(toProjectItem));
      setFromFallback(res.fromFallback);
      setStatus('success');
      console.info(
        `${logTag} ✓ search success q='${q}' items=${res.items.length} fromFallback=${res.fromFallback}`,
      );
    } catch (err) {
      if (err instanceof DOMException && err.name === 'AbortError') return;
      const message = err instanceof Error ? err.message : String(err);
      console.error(`${logTag} ✗ search error q='${q}'`, message);
      setError(message);
      setStatus('error');
    } finally {
      if (inFlightRef.current === controller) inFlightRef.current = null;
    }
  }, [logTag, limit]);

  const sync = useCallback(async () => {
    if (isWarmedRef.current) {
      console.info(`${logTag} sync пропущен — кэш уже прогрет в этой сессии`);
      return;
    }

    // Шаг 1: пробуем прочитать первую страницу из локального кэша backend'а.
    // Если в БД уже есть проекты от предыдущих сессий — sync с Visary не нужен.
    inFlightRef.current?.abort();
    const probeController = new AbortController();
    inFlightRef.current = probeController;
    setStatus('loading');
    setError(null);
    console.info(`${logTag} sync → проверка локального кэша`);

    let cacheHasData = false;
    try {
      const probe = await searchProjects('', { limit, signal: probeController.signal });
      if (probeController.signal.aborted) return;
      if (probe.items.length > 0) {
        cacheHasData = true;
        setData(probe.items.map(toProjectItem));
        setFromFallback(probe.fromFallback);
        setStatus('success');
        isWarmedRef.current = true;
        setIsWarmed(true);
        console.info(
          `${logTag} ✓ кэш уже прогрет (${probe.items.length} items) — sync пропущен`,
        );
        return;
      }
    } catch (err) {
      if (err instanceof DOMException && err.name === 'AbortError') return;
      // Если probe-search упал — пробуем sync, может бэк всё-таки поднимется.
      console.warn(
        `${logTag} probe-search не сработал, пробуем sync`,
        err instanceof Error ? err.message : err,
      );
    }

    if (cacheHasData) return;

    // Шаг 2: кэш пуст → синхронизация с Visary.
    const syncController = new AbortController();
    inFlightRef.current = syncController;
    setStatus('syncing');
    console.info(`${logTag} кэш пуст → sync с Visary`);
    try {
      const res = await syncProjects(syncController.signal);
      if (syncController.signal.aborted) return;
      console.info(`${logTag} ✓ sync OK total=${res.total} upserted=${res.upserted}`);
      isWarmedRef.current = true;
      setIsWarmed(true);
      await runSearch();
    } catch (err) {
      if (err instanceof DOMException && err.name === 'AbortError') return;
      const message = err instanceof Error ? err.message : String(err);
      console.error(`${logTag} ✗ sync error`, message);
      // ⚠️ Даже при failed sync пробуем последний раз search'ом — вдруг кэш
      // частично заполнился, или прошлый sync уже что-то положил.
      try {
        const fallback = await searchProjects('', { limit });
        if (fallback.items.length > 0) {
          setData(fallback.items.map(toProjectItem));
          setFromFallback(fallback.fromFallback);
          setStatus('success');
          isWarmedRef.current = true;
          setIsWarmed(true);
          console.info(
            `${logTag} ⚠️ sync упал, но кэш отдаёт ${fallback.items.length} items — продолжаем`,
          );
          return;
        }
      } catch {
        /* fall through to error state */
      }
      setError(message);
      setStatus('error');
    }
  }, [logTag, limit, runSearch]);

  // Дебаунс: на каждое изменение searchString запускаем поиск, но только если кэш прогрет.
  useEffect(() => {
    if (!isWarmedRef.current) return;
    const t = window.setTimeout(() => {
      void runSearch();
    }, debounceMs);
    return () => window.clearTimeout(t);
  }, [searchString, debounceMs, runSearch]);

  // Cleanup при размонтировании.
  useEffect(() => {
    return () => {
      inFlightRef.current?.abort();
      inFlightRef.current = null;
    };
  }, []);

  const refetch = useCallback(() => {
    void runSearch();
  }, [runSearch]);

  return useMemo(
    () => ({
      data,
      status,
      error,
      fromFallback,
      isWarmed,
      sync,
      refetch,
    }),
    [data, status, error, fromFallback, isWarmed, sync, refetch],
  );
}
