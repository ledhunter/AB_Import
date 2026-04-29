/**
 * Мапперы из API-DTO (`types/api.ts`) в UI-модели (`types/session.ts`).
 *
 * Здесь же — единственное место, где «PascalCase»-статусы превращаются в UI-варианты
 * для подсветки. Все компоненты должны брать `UiSession.variant`, а не делать
 * свой switch по `status`.
 */

import type {
  ApiImportError,
  ApiImportReport,
  ApiImportRow,
  ApiImportSession,
  ApiImportSessionStage,
  ApiImportStatus,
} from '../types/api';
import type {
  SessionStatusVariant,
  UiReport,
  UiReportRow,
  UiRowError,
  UiSession,
  UiSessionStage,
} from '../types/session';

export const toSessionVariant = (status: ApiImportStatus): SessionStatusVariant => {
  switch (status) {
    case 'Pending':
      return 'pending';
    case 'Parsing':
    case 'Validating':
    case 'Applying':
      return 'progress';
    case 'Validated':
      return 'awaiting';
    case 'Applied':
      return 'success';
    case 'Failed':
      return 'failed';
    case 'Cancelled':
      return 'cancelled';
    default:
      // exhaustive check — компилятор подсветит, если в API добавится новый статус
      return 'pending';
  }
};

/**
 * Считает длительность сессии в формате "HH:mm:ss".
 * Возвращает null, если completedAt отсутствует или даты некорректны.
 */
export function computeDuration(
  startedAtIso: string,
  completedAtIso: string | null,
): string | null {
  if (!completedAtIso) return null;
  const start = new Date(startedAtIso).getTime();
  const end = new Date(completedAtIso).getTime();
  if (Number.isNaN(start) || Number.isNaN(end) || end < start) return null;

  const totalSec = Math.round((end - start) / 1000);
  const hh = Math.floor(totalSec / 3600);
  const mm = Math.floor((totalSec % 3600) / 60);
  const ss = totalSec % 60;
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${pad(hh)}:${pad(mm)}:${pad(ss)}`;
}

const toStage = (s: ApiImportSessionStage): UiSessionStage => ({
  kind: s.kind,
  startedAt: s.startedAt,
  completedAt: s.completedAt,
  isSuccess: s.isSuccess,
  progressPercent: s.progressPercent,
  message: s.message,
});

export const toUiSession = (api: ApiImportSession): UiSession => ({
  sessionId: api.sessionId,
  importTypeCode: api.importTypeCode,
  fileName: api.fileName,
  fileFormat: (api.fileFormat || '').toLowerCase(),
  status: api.status,
  variant: toSessionVariant(api.status),
  startedAt: api.startedAt,
  completedAt: api.completedAt,
  duration: computeDuration(api.startedAt, api.completedAt),
  totalRows: api.totalRows,
  successRows: api.successRows,
  errorRows: api.errorRows,
  errorMessage: api.errorMessage,
  stages: (api.stages ?? []).map(toStage),
  // stageProgress инициализируется как null — обновляется через SignalR
  // (`onStageProgress` в useImportSession), а не из REST-снимка сессии.
  stageProgress: null,
});

export const toUiRowError = (e: ApiImportError): UiRowError => ({
  rowNumber: e.sourceRowNumber,
  columnName: e.columnName,
  errorCode: e.errorCode,
  message: e.message,
});

/**
 * Собирает UI-отчёт из ApiImportReport + текущей UiSession.
 *
 * Backend в `/report` не отдаёт session-метаданные (только `sessionId/status/
 * totalRows/successRows/errorRows`), поэтому fileName/startedAt берём из
 * параллельного `getImportSession` или предыдущего snapshot'а — пробрасываем
 * их сюда явно.
 */
export function toUiReport(api: ApiImportReport, session: UiSession): UiReport {
  // Сгруппируем ошибки по rowNumber, чтобы прицепить к строкам.
  const errorsByRow = new Map<number, UiRowError[]>();
  const fileLevelErrors: UiRowError[] = [];
  for (const apiErr of api.errors ?? []) {
    const ui = toUiRowError(apiErr);
    if (ui.rowNumber === 0) {
      fileLevelErrors.push(ui);
      continue;
    }
    const list = errorsByRow.get(ui.rowNumber);
    if (list) list.push(ui);
    else errorsByRow.set(ui.rowNumber, [ui]);
  }

  const rows: UiReportRow[] = (api.rows ?? []).map((r: ApiImportRow) => ({
    rowNumber: r.sourceRowNumber,
    status: r.status,
    errors: errorsByRow.get(r.sourceRowNumber) ?? [],
  }));

  // Если у строк не было записей в `errors`, но есть в errorsByRow по тем же
  // rowNumber'ам — добавляем «осиротевшие» ошибки как отдельные ряды (на случай,
  // когда API вернул ошибку с rowNumber > 0, но самой строки в `rows` нет).
  for (const [rowNumber, errors] of errorsByRow.entries()) {
    if (!rows.some((r) => r.rowNumber === rowNumber)) {
      rows.push({ rowNumber, status: 'Invalid', errors });
    }
  }
  rows.sort((a, b) => a.rowNumber - b.rowNumber);

  return {
    session: {
      ...session,
      // Возможно session уже обновился через SignalR раньше — но если
      // `/report` отдал status свежее, синхронизируем.
      status: api.status,
      variant: toSessionVariant(api.status),
      totalRows: api.totalRows,
      successRows: api.successRows,
      errorRows: api.errorRows,
    },
    rows,
    fileLevelErrors,
    rowsPagination: api.rowsPagination ?? { skip: 0, take: rows.length, total: rows.length },
  };
}
