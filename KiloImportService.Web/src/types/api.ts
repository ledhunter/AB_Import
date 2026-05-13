/**
 * Типы данных, возвращаемых собственным backend (KiloImportService.Api).
 *
 * Camel-case формат как сериализует System.Text.Json по умолчанию (см.
 * KiloImportService.Api/Controllers/ImportsController.cs — анонимные объекты).
 *
 * ⚠️ Эти типы соответствуют тому, что РЕАЛЬНО отдаёт backend. UI-типы из
 * `types/import.ts` — это «обогащённые» презентационные модели, между ними
 * стоит маппер `services/importMappers.ts`.
 */

/**
 * Статус сессии импорта (см. KiloImportService.Api/Domain/Importing/ImportStatus.cs).
 * Сериализуется backend'ом как PascalCase-строка.
 */
export type ApiImportStatus =
  | 'Pending'
  | 'Parsing'
  | 'Validating'
  | 'Validated'
  | 'Applying'
  | 'Applied'
  | 'Failed'
  | 'Cancelled';

/** Этап pipeline (см. ImportStageKind.cs). */
export type ApiImportStageKind = 'Upload' | 'Parse' | 'Validate' | 'Apply';

/** Статус отдельной строки (см. StagedRowStatus.cs). */
export type ApiStagedRowStatus = 'Pending' | 'Valid' | 'Invalid' | 'Applied' | 'Failed';

/** Формат файла. Backend возвращает enum как строку с PascalCase. */
export type ApiFileFormat = 'Csv' | 'Xls' | 'Xlsx' | 'Xlsb';

export interface ApiImportSessionStage {
  kind: ApiImportStageKind;
  startedAt: string; // ISO 8601
  completedAt: string | null;
  isSuccess: boolean;
  progressPercent: number;
  message: string | null;
}

/**
 * Файл, сгенерированный backend'ом по результатам сессии (см. `BuildGeneratedFilesAsync`
 * в ImportsController). Это «доступный артефакт», а не файл на диске — скачивание идёт
 * через `downloadUrl`, backend генерирует содержимое по запросу.
 * Сейчас единственный вид — `budget-xlsx` для «Финмодели», когда в сессии есть бюджетные строки.
 */
export interface ApiGeneratedFile {
  kind: string;
  label: string;
  description: string;
  downloadUrl: string;
  fileName: string;
}

/** Ответ `GET /api/imports/{id}` — состояние сессии. */
export interface ApiImportSession {
  sessionId: string; // GUID
  importTypeCode: string;
  fileName: string;
  fileFormat: ApiFileFormat;
  status: ApiImportStatus;
  startedAt: string;
  completedAt: string | null;
  totalRows: number;
  successRows: number;
  errorRows: number;
  errorMessage: string | null;
  stages: ApiImportSessionStage[];
  /** Файлы, доступные для скачивания (например, бюджет XLSX). Пустой массив если артефактов нет. */
  generatedFiles: ApiGeneratedFile[];
}

export interface ApiImportRow {
  sourceRowNumber: number;
  status: ApiStagedRowStatus;
}

export interface ApiImportError {
  sourceRowNumber: number;
  columnName: string | null;
  errorCode: string;
  message: string;
}

/** Ответ `GET /api/imports/{id}/report` — отчёт сессии (плоский). */
export interface ApiImportReport {
  sessionId: string;
  status: ApiImportStatus;
  totalRows: number;
  successRows: number;
  errorRows: number;
  rows: ApiImportRow[];
  rowsPagination: {
    skip: number;
    take: number;
    total: number;
  };
  errors: ApiImportError[];
}

/** Ответ `POST /api/imports` — sessionId + начальный статус. */
export interface ApiUploadResult {
  sessionId: string;
  status: ApiImportStatus;
}

/** Элемент списка сессий — облегчённое представление (без stages/rows/errors). */
export interface ApiImportSessionSummary {
  sessionId: string;
  importTypeCode: string;
  fileName: string;
  fileFormat: ApiFileFormat;
  status: ApiImportStatus;
  startedAt: string;
  completedAt: string | null;
  totalRows: number;
  successRows: number;
  errorRows: number;
  errorMessage: string | null;
}

/** Ответ `GET /api/imports` — постранично, отсортированно по StartedAt DESC. */
export interface ApiImportSessionsListResponse {
  items: ApiImportSessionSummary[];
  pagination: { skip: number; take: number; total: number };
}

/** Ответ `GET /api/import-types` — реестр типов импорта. */
export interface ApiImportTypeInfo {
  id: string;
  label: string;
  description: string;
  isImplemented: boolean;
}

export interface ApiImportTypesResponse {
  items: ApiImportTypeInfo[];
  total: number;
}
