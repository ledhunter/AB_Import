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
  ApiGeneratedFile,
  ApiImportStageKind,
  ApiImportStatus,
  ApiSheetTotal,
  ApiStagedRowStatus,
} from './api';

/** Презентационный тип для файла, сгенерированного backend'ом по результатам сессии. */
export type UiGeneratedFile = ApiGeneratedFile;

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

/** Live-прогресс по каждому листу многолистового файла. */
export interface UiSheetProgress {
  sheet: string;            // имя листа («Квартиры», «Машиноместа», …)
  stage: StageKind;         // на какой стадии прилетело событие
  currentRow: number;
  totalRows: number;
  percentComplete: number;
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
  /** Live-прогресс из последнего SignalR-события `StageProgress` (общий счётчик). */
  stageProgress: UiStageProgress | null;
  /**
   * Прогресс по каждому листу — обновляется при каждом `StageProgress` событии
   * с непустым `sheet`. Ключ — имя листа, порядок появления сохраняется
   * (используется `Map` под капотом). Один лист = одна строка в UI.
   */
  sheetProgress: UiSheetProgress[];
  /**
   * Файлы, доступные для скачивания по этой сессии (backend генерирует их по запросу).
   * Пустой массив, если артефактов нет.
   */
  generatedFiles: UiGeneratedFile[];
}

export interface UiRowError {
  rowNumber: number;        // 0 — file-level
  sheet: string | null;     // имя листа (для многолистовых импортов; null для file-level)
  columnName: string | null;
  errorCode: string;
  message: string;
}

export interface UiReportRow {
  rowNumber: number;
  sheet: string | null;     // имя листа («Квартиры», «Машиноместа», …); null если файл одностраничный
  status: RowStatus;
  errors: UiRowError[];     // ошибки именно этой строки
  /**
   * Журнал реально выполненных по этой строке действий («Корпус создан»,
   * «Помещение обновлено», «ДДУ найден (не создан)»). Пустой массив для
   * pending/invalid строк, для applied — отражает то, что мапер сделал
   * в Visary. Источник — `apiRow.actions ?? []`.
   */
  actions: string[];
}

/** Карта листов сессии: имя + полное число строк (по всей сессии, без фильтров). */
export type UiSheetTotal = ApiSheetTotal;

export interface UiReport {
  session: UiSession;
  rows: UiReportRow[];
  fileLevelErrors: UiRowError[]; // rowNumber === 0
  /** Учитывает excludeSheets — пагинация считается только по видимым строкам. */
  rowsPagination: { skip: number; take: number; total: number };
  /**
   * Полная карта листов сессии (включая свёрнутые). Используется UI'ем для
   * рисования заголовков сворачиваемых листов с числом строк, даже когда
   * сами строки исключены из выборки.
   */
  sheetTotals: UiSheetTotal[];
}

/**
 * Лёгкая сводка сессии для списка истории — без stages/sheetProgress/errors.
 * Содержит ровно то, что отдаёт `GET /api/imports`, + вычисленный variant и duration.
 */
export interface UiSessionSummary {
  sessionId: string;
  importTypeCode: string;
  fileName: string;
  fileFormat: string;             // lower-case ('csv' | 'xls' | 'xlsx' | 'xlsb')
  status: SessionStatus;
  variant: SessionStatusVariant;
  startedAt: string;
  completedAt: string | null;
  duration: string | null;
  totalRows: number;
  successRows: number;
  errorRows: number;
  errorMessage: string | null;
  /** ID проекта Visary (null — не был указан при загрузке). */
  projectId: number | null;
  /** Название проекта (из локального кэша). null если кэш не содержит этой записи. */
  projectName: string | null;
}
