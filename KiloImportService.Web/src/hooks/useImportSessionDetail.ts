/**
 * useImportSessionDetail — read-only загрузка деталей одной сессии для страницы
 * истории. В отличие от `useImportSession`, тут нет SignalR/state-машины,
 * apply/cancel и работы с FormData — только REST-снимок.
 *
 * Используется на странице «История импортов» при раскрытии конкретной сессии.
 */

import { useCallback, useEffect, useRef, useState } from 'react';
import {
  getImportReport,
  getImportSession,
  ImportsApiError,
} from '../services/importsService';
import { toUiReport, toUiSession } from '../services/importMappers';
import type { UiReport, UiSession } from '../types/session';
import { REPORT_PAGE_SIZE } from './useImportSession';

export interface UseImportSessionDetailState {
  session: UiSession | null;
  report: UiReport | null;
  loading: boolean;
  error: string | null;
  reload: () => Promise<void>;
  /** Переключение страницы построчного отчёта (skip строк от начала). */
  loadReportPage: (skip: number) => Promise<void>;
}

/** Статусы, при которых имеет смысл подтягивать отчёт. */
const REPORT_LOAD_STATUSES = new Set([
  'Validated',
  'Applied',
  'Failed',
  'Cancelled',
] as const);

export function useImportSessionDetail(
  sessionId: string | null,
): UseImportSessionDetailState {
  const [session, setSession] = useState<UiSession | null>(null);
  const [report, setReport] = useState<UiReport | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const abortRef = useRef<AbortController | null>(null);

  const load = useCallback(async (id: string, skip = 0) => {
    abortRef.current?.abort();
    const ctrl = new AbortController();
    abortRef.current = ctrl;

    setLoading(true);
    setError(null);
    try {
      const apiSession = await getImportSession(id, { signal: ctrl.signal });
      if (ctrl.signal.aborted) return;
      const ui = toUiSession(apiSession);
      setSession(ui);

      if (REPORT_LOAD_STATUSES.has(
        ui.status as 'Validated' | 'Applied' | 'Failed' | 'Cancelled',
      )) {
        const apiReport = await getImportReport(id, {
          signal: ctrl.signal,
          skip,
          take: REPORT_PAGE_SIZE,
        });
        if (ctrl.signal.aborted) return;
        setReport(toUiReport(apiReport, ui));
      } else {
        setReport(null);
      }
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
  }, []);

  useEffect(() => {
    if (!sessionId) {
      setSession(null);
      setReport(null);
      setError(null);
      return;
    }
    void load(sessionId);
    return () => abortRef.current?.abort();
  }, [sessionId, load]);

  const reload = useCallback(async () => {
    if (sessionId) await load(sessionId);
  }, [sessionId, load]);

  const loadReportPage = useCallback(
    async (skip: number) => {
      if (sessionId) await load(sessionId, skip);
    },
    [sessionId, load],
  );

  return { session, report, loading, error, reload, loadReportPage };
}
