/**
 * useImportSession — управляет жизненным циклом одной сессии импорта в UI.
 *
 * Состояния:
 *   - 'idle'      — сессия не создана (форма)
 *   - 'uploading' — POST /api/imports в процессе
 *   - 'tracking'  — sessionId есть, подписан на SignalR; status обновляется
 *                   live (Pending → Parsing → Validating → Validated|Failed → ...)
 *   - 'applying'  — POST /api/imports/{id}/apply в процессе
 *   - 'completed' — Applied | Failed | Cancelled — финальное состояние
 *   - 'error'     — ошибка fetch/upload (до получения sessionId или после)
 *
 * UI-слой (App.tsx) не должен думать про SignalR-события — он смотрит только
 * на `phase` и `session/report`.
 */

import { useCallback, useEffect, useRef, useState } from 'react';
import {
  applyImport,
  cancelImport,
  getImportReport,
  getImportSession,
  ImportsApiError,
  uploadImport,
  type UploadImportPayload,
} from '../services/importsService';
import { createImportsHub } from '../services/importsHub';
import {
  toSessionVariant,
  toUiReport,
  toUiSession,
} from '../services/importMappers';
import type { UiReport, UiSession } from '../types/session';

export type ImportPhase =
  | 'idle'
  | 'uploading'
  | 'tracking'
  | 'applying'
  | 'completed'
  | 'error';

export interface UseImportSessionState {
  phase: ImportPhase;
  session: UiSession | null;
  report: UiReport | null;
  error: string | null;
  /** Стартует импорт: загружает файл и подписывается на прогресс. */
  start: (payload: UploadImportPayload) => Promise<void>;
  /** Применить валидные строки в visary_db (только из status=Validated). */
  apply: () => Promise<void>;
  /** Отменить сессию (только до Apply). */
  cancel: () => Promise<void>;
  /** Сбросить состояние и подготовиться к новому импорту. */
  reset: () => void;
}

const FINAL_STATUSES = new Set(['Applied', 'Failed', 'Cancelled'] as const);
const REPORT_LOAD_STATUSES = new Set([
  'Validated',
  'Applied',
  'Failed',
  'Cancelled',
] as const);

const LOG_TAG = '[useImportSession]';

export function useImportSession(): UseImportSessionState {
  const [phase, setPhase] = useState<ImportPhase>('idle');
  const [session, setSession] = useState<UiSession | null>(null);
  const [report, setReport] = useState<UiReport | null>(null);
  const [error, setError] = useState<string | null>(null);

  // Храним hub в ref, чтобы при unmount остановить и не пересоздавать на ререндерах.
  const hubRef = useRef<Awaited<ReturnType<typeof createImportsHub>> | null>(null);
  // Текущий sessionId — для cleanup unmount и проверок в коллбэках.
  const sessionIdRef = useRef<string | null>(null);
  // Latest session — чтобы коллбэки SignalR могли строить новый объект из свежего.
  const sessionLatestRef = useRef<UiSession | null>(null);
  // Запросы report'а нужно отменять при новом sessionId / unmount.
  const reportAbortRef = useRef<AbortController | null>(null);

  // Синхронизируем ref'ы с state (запись только в effect — react-hooks/refs).
  useEffect(() => {
    sessionLatestRef.current = session;
  }, [session]);

  useEffect(() => {
    sessionIdRef.current = session?.sessionId ?? null;
  }, [session?.sessionId]);

  // Cleanup на unmount.
  useEffect(() => {
    return () => {
      reportAbortRef.current?.abort();
      void hubRef.current?.stop();
      hubRef.current = null;
    };
  }, []);

  /** Загрузить актуальный отчёт. Не падает наружу — пишет в state.error. */
  const loadReport = useCallback(async (sessionId: string) => {
    reportAbortRef.current?.abort();
    const ctrl = new AbortController();
    reportAbortRef.current = ctrl;

    try {
      const apiReport = await getImportReport(sessionId, { signal: ctrl.signal });
      if (ctrl.signal.aborted) return;
      const currentSession = sessionLatestRef.current;
      if (!currentSession || currentSession.sessionId !== sessionId) {
        return; // переключились на новую сессию, отчёт уже неактуален
      }
      setReport(toUiReport(apiReport, currentSession));
      console.info(`${LOG_TAG} report loaded: rows=${apiReport.rows.length} errors=${apiReport.errors.length}`);
    } catch (err) {
      if (err instanceof DOMException && err.name === 'AbortError') return;
      const message = err instanceof Error ? err.message : String(err);
      console.warn(`${LOG_TAG} loadReport failed:`, message);
      setError(message);
    }
  }, []);

  /** Pull session state из backend (для синхронизации, fallback при потере SignalR). */
  const pullSession = useCallback(async (sessionId: string) => {
    try {
      const apiSession = await getImportSession(sessionId);
      const ui = toUiSession(apiSession);
      setSession(ui);
      // Если статус финальный — phase=completed.
      if (FINAL_STATUSES.has(ui.status as 'Applied' | 'Failed' | 'Cancelled')) {
        setPhase('completed');
      }
      // Если есть смысл — загружаем отчёт.
      if (REPORT_LOAD_STATUSES.has(ui.status as 'Validated' | 'Applied' | 'Failed' | 'Cancelled')) {
        await loadReport(sessionId);
      }
    } catch (err) {
      if (err instanceof DOMException && err.name === 'AbortError') return;
      const message = err instanceof Error ? err.message : String(err);
      console.warn(`${LOG_TAG} pullSession failed:`, message);
    }
  }, [loadReport]);

  const start = useCallback<UseImportSessionState['start']>(
    async (payload) => {
      // Сбрасываем предыдущее состояние.
      reportAbortRef.current?.abort();
      void hubRef.current?.stop();
      hubRef.current = null;

      setError(null);
      setReport(null);
      setSession(null);
      setPhase('uploading');

      let sessionId: string;
      try {
        const upload = await uploadImport(payload);
        sessionId = upload.sessionId;
        console.info(`${LOG_TAG} upload OK: sessionId=${sessionId}, status=${upload.status}`);
      } catch (err) {
        const message =
          err instanceof ImportsApiError
            ? err.message
            : err instanceof Error
              ? err.message
              : String(err);
        console.error(`${LOG_TAG} upload failed:`, message);
        setError(message);
        setPhase('error');
        return;
      }

      setPhase('tracking');

      // Поднимаем SignalR подключение.
      try {
        const hub = await createImportsHub({
          onSessionStatus: (e) => {
            if (e.sessionId !== sessionIdRef.current) return;
            const prev = sessionLatestRef.current;
            if (!prev) return;
            const next: UiSession = {
              ...prev,
              status: e.status,
              variant: toSessionVariant(e.status),
            };
            setSession(next);

            // Финальный статус — переходим в completed + дёргаем отчёт.
            const isFinal = FINAL_STATUSES.has(
              e.status as 'Applied' | 'Failed' | 'Cancelled',
            );
            const needsReport = REPORT_LOAD_STATUSES.has(
              e.status as 'Validated' | 'Applied' | 'Failed' | 'Cancelled',
            );
            if (isFinal) setPhase('completed');
            if (needsReport) {
              // Pull всех данных через REST для целостности.
              void pullSession(e.sessionId);
            }
          },
          onStageStarted: (e) => {
            if (e.sessionId !== sessionIdRef.current) return;
            console.info(`${LOG_TAG} stage started: ${e.stage}`);
          },
          onStageCompleted: (e) => {
            if (e.sessionId !== sessionIdRef.current) return;
            console.info(`${LOG_TAG} stage completed: ${e.stage}`);
            // Очищаем live-прогресс при завершении стадии — следующая стадия
            // запустится с чистым счётчиком.
            const prev = sessionLatestRef.current;
            if (prev && prev.sessionId === e.sessionId && prev.stageProgress) {
              setSession({ ...prev, stageProgress: null });
            }
          },
          onStageProgress: (e) => {
            if (e.sessionId !== sessionIdRef.current) return;
            const prev = sessionLatestRef.current;
            if (!prev) return;
            setSession({
              ...prev,
              stageProgress: {
                stage: e.stage,
                currentRow: e.currentRow,
                totalRows: e.totalRows,
                percentComplete: e.percentComplete,
                sheet: e.sheet ?? null,
              },
            });
          },
        });
        hubRef.current = hub;
        await hub.joinSession(sessionId);
      } catch (err) {
        const message = err instanceof Error ? err.message : String(err);
        console.warn(`${LOG_TAG} hub failed (продолжим polling):`, message);
        // Не считаем фатальной ошибкой — pullSession ниже даст состояние.
      }

      // Сразу подтягиваем session, чтобы UI не висел в `tracking` без данных.
      await pullSession(sessionId);
    },
    [pullSession],
  );

  const apply = useCallback<UseImportSessionState['apply']>(async () => {
    const sessionId = sessionIdRef.current;
    if (!sessionId) return;
    setPhase('applying');
    setError(null);
    try {
      await applyImport(sessionId);
      // Финальный статус (Applied или Failed) придёт через SignalR; на всякий
      // случай — pull через 500ms для синхронизации.
      setTimeout(() => {
        void pullSession(sessionId);
      }, 500);
    } catch (err) {
      const message = err instanceof Error ? err.message : String(err);
      console.error(`${LOG_TAG} apply failed:`, message);
      setError(message);
      setPhase('error');
    }
  }, [pullSession]);

  const cancel = useCallback<UseImportSessionState['cancel']>(async () => {
    const sessionId = sessionIdRef.current;
    if (!sessionId) return;
    try {
      await cancelImport(sessionId);
      await pullSession(sessionId);
    } catch (err) {
      const message = err instanceof Error ? err.message : String(err);
      console.error(`${LOG_TAG} cancel failed:`, message);
      setError(message);
    }
  }, [pullSession]);

  const reset = useCallback<UseImportSessionState['reset']>(() => {
    reportAbortRef.current?.abort();
    void hubRef.current?.stop();
    hubRef.current = null;
    setPhase('idle');
    setSession(null);
    setReport(null);
    setError(null);
  }, []);

  return { phase, session, report, error, start, apply, cancel, reset };
}
