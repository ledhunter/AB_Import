// Открытый union: реальный список типов приходит из IMPORT_TYPES (могут добавляться)
export type ImportType = string;
export type FileFormat = 'csv' | 'xls' | 'xlsb' | 'xlsx';

export interface ImportTypeOption {
  id: string;
  label: string;
  description?: string;
}
export type ImportStatus = 'idle' | 'queued' | 'parsing' | 'processing' | 'completed' | 'completedWithWarnings' | 'failed' | 'cancelled';
export type EntityAction = 'Created' | 'Updated' | 'Skipped' | 'Error';
export type RowStatus = 'success' | 'warning' | 'error';

/**
 * Параметры запуска импорта.
 * Формат файла определяется на backend по расширению, не передаётся.
 * Даты начала/окончания импорта фиксируются backend'ом автоматически (см. ImportReport.startedAt/completedAt).
 */
export interface ImportRequest {
  projectId: number;
  siteId: number;
  importType: ImportType;
  file: File;
}

export interface ImportProgress {
  importId: string;
  currentRow: number;
  totalRows: number;
  currentSheet: string;
  percentComplete: number;
}

export interface ImportSummary {
  totalSheets: number;
  totalRows: number;
  roomsCreated: number;
  roomsUpdated: number;
  roomsSkipped: number;
  shareAgreementsCreated: number;
  shareAgreementsUpdated: number;
  shareAgreementsSkipped: number;
  errorsCount: number;
  warningsCount: number;
}

export interface EntityDestination {
  entity: string;
  action: EntityAction;
  entityId: number | null;
  entityTitle: string | null;
  targetField: string | null;
}

export interface ReportWarning {
  field: string;
  message: string;
}

export interface ReportError {
  field: string;
  message: string;
}

export interface ReportRow {
  rowNumber: number;
  sheet: string;
  status: RowStatus;
  sourceData: Record<string, string | null>;
  destinations: EntityDestination[];
  warnings: ReportWarning[];
  errors: ReportError[];
}

export interface ImportReport {
  importId: string;
  status: ImportStatus;
  importType: ImportType;
  fileFormat: FileFormat;
  fileName: string;
  startedAt: string;
  completedAt: string | null;
  duration: string | null;
  summary: ImportSummary;
  rows: ReportRow[];
  pagination: {
    page: number;
    pageSize: number;
    totalPages: number;
    totalItems: number;
  };
}
