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
  /** URL для скачивания файла (GET). */
  downloadUrl: string | null;
  /**
   * Зарезервировано под action-кнопки (POST без скачивания). Сейчас неактивно: загрузка
   * бюджета в Visary происходит автоматически на стадии Apply (FinModelImportMapper),
   * поэтому backend поле не выставляет. Сохранено для совместимости с будущими сценариями.
   */
  actionUrl?: string | null;
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
  sheet: string | null;
  status: ApiStagedRowStatus;
  /**
   * Журнал действий, реально выполненных по этой строке в Apply-фазе.
   * Заполняется маппером — например, `["Корпус создан (1.1)",
   * "Помещение обновлено (№15)", "ДДУ найден (не создан, №ДДУ-42)"]`.
   * `null` — маппер не сообщил per-row actions (старая сессия / маппер
   * без поддержки журнала).
   */
  actions: string[] | null;
}

export interface ApiImportError {
  sourceRowNumber: number;
  sheet: string | null;
  columnName: string | null;
  errorCode: string;
  message: string;
}

/**
 * Полная карта листов сессии: имя листа (null — одностраничные импорты) + общее
 * число строк. Возвращается ВСЕГДА по всем листам, независимо от `excludeSheets`,
 * чтобы UI мог отрисовать заголовки свёрнутых листов с их счётчиками.
 */
export interface ApiSheetTotal {
  sheet: string | null;
  total: number;
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
    /** Уже учитывает excludeSheets: `total` = число строк ВИДИМЫХ листов. */
    skip: number;
    take: number;
    total: number;
  };
  /** Все листы сессии с числом строк — для рисования сворачиваемых заголовков. */
  sheetTotals: ApiSheetTotal[];
  /**
   * Счётчики по action-меткам (created/updated/skipped) по ВСЕЙ сессии,
   * не по текущей странице. Категоризация по главной сущности строки —
   * см. doc 98 v1.1. У старого backend'а поля может не быть → `undefined`.
   */
  actionTotals?: ApiActionTotals;
  /**
   * Счётчики по StagedRowStatus (all/valid/invalid/applied/failed) по ВСЕЙ сессии.
   * UI показывает их в status-фильтрах (Все/Валидные/С ошибками/…) — они теперь
   * session-wide и совпадают с верхней панелью отчёта. У старого backend'а отсутствует
   * → фронт фоллбэчит на page-level подсчёт по `report.rows`. См. doc 98 v1.3.
   */
  statusTotals?: ApiStatusTotals;
  errors: ApiImportError[];
}

export interface ApiActionTotals {
  created: number;
  updated: number;
  skipped: number;
}

export interface ApiStatusTotals {
  all: number;
  valid: number;
  invalid: number;
  applied: number;
  failed: number;
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
  /** ID проекта Visary, выбранного при загрузке сессии (null — не указан). */
  projectId: number | null;
  /** Название проекта из локального кэша Visary (null — проект пропал из кэша). */
  projectName: string | null;
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
