/**
 * Загружает список типов импорта из backend (`GET /api/import-types`).
 *
 * В отличие от `useProjects` — eager loading: выполняется один раз при mount,
 * потому что список нужен сразу для отображения формы (Select типов импорта).
 *
 * Контракт совпадает по форме с другими lazy-хуками: `{ data, status, error, refetch }`.
 */

import { useCallback, useEffect, useRef, useState } from 'react';
import { getImportTypes } from '../services/importsService';
import type { ApiImportTypeInfo } from '../types/api';

export type ImportTypesStatus = 'loading' | 'success' | 'error';

export interface UseImportTypesState {
  data: ApiImportTypeInfo[];
  status: ImportTypesStatus;
  error: string | null;
  refetch: () => void;
}

const LOG_TAG = '[useImportTypes]';

export function useImportTypes(): UseImportTypesState {
  const [data, setData] = useState<ApiImportTypeInfo[]>([]);
  const [status, setStatus] = useState<ImportTypesStatus>('loading');
  const [error, setError] = useState<string | null>(null);
  const inFlightRef = useRef<AbortController | null>(null);

  const run = useCallback(() => {
    inFlightRef.current?.abort();
    const ctrl = new AbortController();
    inFlightRef.current = ctrl;
    setStatus('loading');
    setError(null);
    console.info(`${LOG_TAG} fetching…`);
    getImportTypes({ signal: ctrl.signal })
      .then((res) => {
        if (ctrl.signal.aborted) return;
        setData(res.items ?? []);
        setStatus('success');
        console.info(`${LOG_TAG} ✓ loaded ${res.items?.length ?? 0} types`);
      })
      .catch((err) => {
        if (err instanceof DOMException && err.name === 'AbortError') return;
        const message = err instanceof Error ? err.message : String(err);
        setError(message);
        setStatus('error');
        console.warn(`${LOG_TAG} ✗ failed:`, message);
      });
  }, []);

  // Eager-load: типы импорта нужны сразу при монтировании формы, чтобы Select
  // показал опции без дополнительного клика. setState внутри run() — это
  // намеренная синхронизация с внешним источником (REST), правило
  // react-hooks/set-state-in-effect здесь даёт ложноположительное срабатывание.
  useEffect(() => {
    // eslint-disable-next-line react-hooks/set-state-in-effect
    run();
    return () => {
      inFlightRef.current?.abort();
    };
  }, [run]);

  return { data, status, error, refetch: run };
}
