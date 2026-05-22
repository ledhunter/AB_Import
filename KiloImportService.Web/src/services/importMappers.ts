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
  ApiImportSessionSummary,
  ApiImportStatus,
} from '../types/api';
import type {
  SessionStatusVariant,
  UiReport,
  UiReportRow,
  UiRowError,
  UiSession,
  UiSessionStage,
  UiSessionSummary,
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
  // stageProgress / sheetProgress инициализируются пустыми — обновляются через
  // SignalR (`onStageProgress` в useImportSession), а не из REST-снимка сессии.
  stageProgress: null,
  sheetProgress: [],
  generatedFiles: api.generatedFiles ?? [],
});

export const toUiSessionSummary = (api: ApiImportSessionSummary): UiSessionSummary => ({
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
  // Старые сессии в БД (до миграции на сохранение projectId) могут вернуть null —
  // считаем это валидным состоянием «проект не указан».
  projectId: api.projectId ?? null,
  projectName: api.projectName ?? null,
});

export const toUiRowError = (e: ApiImportError): UiRowError => ({
  rowNumber: e.sourceRowNumber,
  sheet: e.sheet,
  columnName: e.columnName,
  errorCode: e.errorCode,
  message: e.message,
});

const rowKey = (sheet: string | null | undefined, rowNumber: number): string =>
  `${sheet ?? ''}::${rowNumber}`;

/**
 * Собирает UI-отчёт из ApiImportReport + текущей UiSession.
 *
 * Backend в `/report` не отдаёт session-метаданные (только `sessionId/status/
 * totalRows/successRows/errorRows`), поэтому fileName/startedAt берём из
 * параллельного `getImportSession` или предыдущего snapshot'а — пробрасываем
 * их сюда явно.
 */
export function toUiReport(api: ApiImportReport, session: UiSession): UiReport {
  // Ошибки группируем по (Sheet, RowNumber): уникальность строки в многолистовом
  // импорте определяется именно этой парой (см. doc_project/72-multi-sheet-import.md).
  const errorsByRow = new Map<string, UiRowError[]>();
  const fileLevelErrors: UiRowError[] = [];
  for (const apiErr of api.errors ?? []) {
    const ui = toUiRowError(apiErr);
    if (ui.rowNumber === 0) {
      fileLevelErrors.push(ui);
      continue;
    }
    const key = rowKey(ui.sheet, ui.rowNumber);
    const list = errorsByRow.get(key);
    if (list) list.push(ui);
    else errorsByRow.set(key, [ui]);
  }

  const rows: UiReportRow[] = (api.rows ?? []).map((r: ApiImportRow) => ({
    rowNumber: r.sourceRowNumber,
    sheet: r.sheet,
    status: r.status,
    errors: errorsByRow.get(rowKey(r.sheet, r.sourceRowNumber)) ?? [],
    actions: r.actions ?? [],
  }));

  // Раньше тут подмешивали «осиротевшие» ошибки (rowNumber, не попавший в `api.rows`
  // текущей страницы), пушили их отдельными строками. Это раздувало страницу до
  // `take + N_orphan_errors` (например, скрин «1–693 из 1735»: 50 + 643 ошибочных).
  // Пагинация перестаёт быть «по 50 строк» — а заказчик именно этого хотел.
  // Теперь страница содержит ровно те `rows`, что вернул backend; ошибка показывается
  // у своей строки (через `errorsByRow.get`), а строки-без-rows — на странице,
  // где эти rowNumber окажутся в выборке. См. doc_project/102-page-size-exact-50.md.
  rows.sort((a, b) => {
    const sa = a.sheet ?? '';
    const sb = b.sheet ?? '';
    if (sa !== sb) return sa.localeCompare(sb, 'ru');
    return a.rowNumber - b.rowNumber;
  });

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
    // sheetTotals может отсутствовать у старого backend'а — отрисуем без счётчиков свёрнутых.
    sheetTotals: api.sheetTotals ?? [],
    // actionTotals — счётчики created/updated/skipped по ВСЕЙ сессии (doc 98 v1.2).
    // Если backend старый — нули, табы фильтров просто не отрисуются (count > 0 gate).
    actionTotals: api.actionTotals ?? { created: 0, updated: 0, skipped: 0 },
    // statusTotals — session-wide all/valid/invalid/applied/failed (doc 98 v1.3).
    // null означает «backend не отдал», UI вычислит по page-level (legacy fallback).
    statusTotals: api.statusTotals ?? null,
  };
}
