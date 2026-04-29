/**
 * UI-модели сессии импорта и отчёта — отражают то, что РЕАЛЬНО отдаёт
 * backend KiloImportService.Api. Camel-case, понятные русским.
 *
 * Маппинг из API-формата (`types/api.ts`) делается в `services/importMappers.ts`.
 *
 * ⚠️ Эти типы не содержат презентационных «обогащений» (roomsCreated/Updated/
 * destinations/sourceData/sheet) — backend их не агрегирует. Когда такая
 * агрегация появится (например, отдельная таблица `ImportRowAction`), эти
 * типы будут расширены и маппер обновится.
 */

import type {
  ApiImportStageKind,
  ApiImportStatus,
  ApiStagedRowStatus,
} from './api';

export type SessionStatus = ApiImportStatus;
export type StageKind = ApiImportStageKind;
export type RowStatus = ApiStagedRowStatus;

/** Эта же шкала в UI-цветах. */
export type SessionStatusVariant =
  | 'pending'      // Pending
  | 'progress'     // Parsing | Validating | Applying
  | 'awaiting'     // Validated (готово, ждём apply)
  | 'success'      // Applied
  | 'failed'       // Failed
  | 'cancelled';   // Cancelled

export interface UiSessionStage {
  kind: StageKind;
  startedAt: string;          // ISO
  completedAt: string | null;
  isSuccess: boolean;
  progressPercent: number;
  message: string | null;
}

/** Текущий прогресс по строкам внутри стадии (Validate/Apply). */
export interface UiStageProgress {
  stage: StageKind;
  currentRow: number;
  totalRows: number;
  percentComplete: number;
  sheet: string | null;
}

/** Состояние сессии (без подробных строк). */
export interface UiSession {
  sessionId: string;
  importTypeCode: string;
  fileName: string;
  fileFormat: string;          // 'csv' | 'xls' | 'xlsx' | 'xlsb' (lowercased)
  status: SessionStatus;
  variant: SessionStatusVariant;
  startedAt: string;
  completedAt: string | null;
  duration: string | null;     // "HH:mm:ss", вычисляется на UI
  totalRows: number;
  successRows: number;
  errorRows: number;
  errorMessage: string | null;
  stages: UiSessionStage[];
  /** Live-прогресс из последнего SignalR-события `StageProgress`. */
  stageProgress: UiStageProgress | null;
}

export interface UiRowError {
  rowNumber: number;        // 0 — file-level
  columnName: string | null;
  errorCode: string;
  message: string;
}

export interface UiReportRow {
  rowNumber: number;
  status: RowStatus;
  errors: UiRowError[];     // ошибки именно этой строки
}

export interface UiReport {
  session: UiSession;
  rows: UiReportRow[];
  fileLevelErrors: UiRowError[]; // rowNumber === 0
  rowsPagination: { skip: number; take: number; total: number };
}
