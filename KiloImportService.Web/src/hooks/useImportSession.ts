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
import type { UiReport, UiSession, UiSheetProgress } from '../types/session';

/**
 * Иммутабельный upsert прогресса по листу: если лист уже в массиве — заменяем
 * запись по индексу (сохраняем порядок появления); если нет — добавляем в конец.
 */
function upsertSheetProgress(
  current: UiSheetProgress[],
  next: UiSheetProgress,
): UiSheetProgress[] {
  const idx = current.findIndex(
    (p) => p.sheet.toLowerCase() === next.sheet.toLowerCase(),
  );
  if (idx === -1) return [...current, next];
  const copy = current.slice();
  copy[idx] = next;
  return copy;
}

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
  /**
   * Перейти на страницу отчёта (по `skip` строк от начала, размер страницы
   * фиксирован — <c>REPORT_PAGE_SIZE</c>). Опциональный <c>excludeSheets</c>
   * исключает указанные листы из выборки и `total` (для клиентского
   * сворачивания листов в UI). Использует currentSessionId, молча no-op
   * если сессии нет.
   */
  loadReportPage: (skip: number, options?: { excludeSheets?: string[] }) => Promise<void>;
}

/**
 * Размер страницы построчного отчёта. Синхронизирован с backend-дефолтом
 * (<c>ImportsController.GetReport</c>): если меняешь — меняй обе стороны.
 * Уменьшено со 100 до 50 (2026-05-20) для более удобной навигации по
 * многолистовым отчётам — см.
 * <c>doc_project/95-history-project-filter-and-collapsible-sheets.md</c>.
 */
export const REPORT_PAGE_SIZE = 50;

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

  // Запомненный набор свёрнутых листов — нужен, чтобы pullSession (вызывается из
  // SignalR-хэндлеров) перезагружал отчёт с тем же фильтром. Иначе после прихода
  // финального статуса свёрнутые листы «развернутся» сами.
  const excludeSheetsRef = useRef<string[]>([]);

  /** Загрузить актуальный отчёт. Не падает наружу — пишет в state.error. */
  const loadReport = useCallback(async (
    sessionId: string,
    skip = 0,
    excludeSheets: string[] = excludeSheetsRef.current,
  ) => {
    reportAbortRef.current?.abort();
    const ctrl = new AbortController();
    reportAbortRef.current = ctrl;

    excludeSheetsRef.current = excludeSheets;

    try {
      const apiReport = await getImportReport(sessionId, {
        signal: ctrl.signal,
        skip,
        take: REPORT_PAGE_SIZE,
        excludeSheets,
      });
      if (ctrl.signal.aborted) return;
      const currentSession = sessionLatestRef.current;
      if (!currentSession || currentSession.sessionId !== sessionId) {
        return; // переключились на новую сессию, отчёт уже неактуален
      }
      setReport(toUiReport(apiReport, currentSession));
      console.info(`${LOG_TAG} report loaded: skip=${skip} rows=${apiReport.rows.length} errors=${apiReport.errors.length} excludeSheets=[${excludeSheets.join(',')}]`);
    } catch (err) {
      if (err instanceof DOMException && err.name === 'AbortError') return;
      const message = err instanceof Error ? err.message : String(err);
      console.warn(`${LOG_TAG} loadReport failed:`, message);
      setError(message);
    }
  }, []);

  /** Переключение страницы отчёта — публичный метод для UI. */
  const loadReportPage = useCallback(async (
    skip: number,
    options?: { excludeSheets?: string[] },
  ) => {
    const sid = sessionIdRef.current;
    if (!sid) return;
    const exclude = options?.excludeSheets ?? excludeSheetsRef.current;
    await loadReport(sid, skip, exclude);
  }, [loadReport]);

  /** Pull session state из backend (для синхронизации, fallback при потере SignalR). */
  const pullSession = useCallback(async (sessionId: string) => {
    try {
      const apiSession = await getImportSession(sessionId);
      const ui = toUiSession(apiSession);
      // Сохраняем live-прогресс по листам: он накапливается в UI из SignalR
      // событий, а REST-снимок про листы ничего не знает.
      const previous = sessionLatestRef.current;
      if (previous && previous.sessionId === ui.sessionId) {
        ui.sheetProgress = previous.sheetProgress;
        ui.stageProgress = previous.stageProgress;
      }
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
      // Сбрасываем фильтр свёрнутых листов: новый импорт начинается с чистой картины.
      excludeSheetsRef.current = [];

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
          // Во ВСЕХ хэндлерах SignalR используем функциональный setSession(prev => ...).
          // Объяснение: `sessionLatestRef.current` обновляется через useEffect,
          // т.е. ПОСЛЕ коммита render'а; если два события прилетят в одну
          // micro-task'у (что для SignalR-троттлинга случается), второй
          // хэндлер увидит несвежий ref и перетрёт изменения первого.
          // Функциональный setState получает гарантированно актуальный prev
          // из очереди React и решает race condition.
          onSessionStatus: (e) => {
            if (e.sessionId !== sessionIdRef.current) return;
            setSession((prev) => {
              if (!prev) return prev;
              return {
                ...prev,
                status: e.status,
                variant: toSessionVariant(e.status),
              };
            });

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
            // запустится с чистым счётчиком. sheetProgress НЕ сбрасываем —
            // он должен показать финальную картину по листам.
            setSession((prev) => {
              if (!prev || prev.sessionId !== e.sessionId || !prev.stageProgress) {
                return prev;
              }
              return { ...prev, stageProgress: null };
            });
          },
          onStageProgress: (e) => {
            if (e.sessionId !== sessionIdRef.current) return;
            setSession((prev) => {
              if (!prev) return prev;
              // Накапливаем прогресс по листу: если лист уже встречался —
              // обновляем его счётчики; если нет — добавляем в конец (порядок
              // появления листов в файле сохраняется). Без `sheet` — общий
              // прогресс, в sheetProgress не пишем.
              const nextSheetProgress = e.sheet
                ? upsertSheetProgress(prev.sheetProgress, {
                    sheet: e.sheet,
                    stage: e.stage,
                    currentRow: e.currentRow,
                    totalRows: e.totalRows,
                    percentComplete: e.percentComplete,
                  })
                : prev.sheetProgress;
              return {
                ...prev,
                stageProgress: {
                  stage: e.stage,
                  currentRow: e.currentRow,
                  totalRows: e.totalRows,
                  percentComplete: e.percentComplete,
                  sheet: e.sheet ?? null,
                },
                sheetProgress: nextSheetProgress,
              };
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

  return { phase, session, report, error, start, apply, cancel, reset, loadReportPage };
}
