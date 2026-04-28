// =====================================================================
// Visary ListView API (POST /api/visary/listview/{mnemonic})
// =====================================================================

/**
 * Запрос к Visary ListView API.
 * Поля сделаны как в реальном API (PascalCase) — сериализуются как есть.
 */
export interface ListViewRequest {
  Mnemonic: string;
  PageSkip: number;
  PageSize: number;
  Columns: string[];
  Sorts: string;                  // JSON-строка вида "[{\"selector\":\"ID\",\"desc\":true}]"
  Hidden: boolean;
  ExtraFilter: string | null;
  SearchString: string;
  AssociatedID: number | null;
}

/**
 * Универсальный ответ Visary ListView API.
 * T — тип конкретной строки (Project / Site / ...).
 *
 * Реальная структура ответа от Visary:
 *   { Data: T[], Total: number, Summaries: unknown[] }
 *
 * Для устойчивости поддерживаем и альтернативные имена (`Items`/`TotalCount`),
 * на случай если они встретятся на других эндпоинтах.
 */
export interface ListViewResponse<T> {
  Data?: T[];
  Items?: T[];
  items?: T[];
  Total?: number;
  TotalCount?: number;
  totalCount?: number;
  Summaries?: unknown[];
}

// =====================================================================
// Construction Project (объект из Visary listview)
// =====================================================================

/**
 * Сырая строка проекта строительства из Visary ListView.
 * Имена полей соответствуют запрошенным `Columns` (PascalCase).
 */
export interface ConstructionProjectRaw {
  ID: number;
  IdentifierKK?: string | null;
  IdentifierZPLM?: string | null;
  Title: string;
  Type?: string | null;
  Phase?: string | null;
  Region?: string | null;
  Town?: string | null;
  Developer?: string | null;
  ProjectManagment?: string | null;
  Sponsor?: string | null;
  ProjectPeriod?: string | null;
  RowVersion?: string | number | null;
}

// =====================================================================
// Нормализованные UI-типы
// =====================================================================

/**
 * Нормализованный проект для UI (после маппинга из Visary API).
 */
export interface ProjectItem {
  id: number;
  title: string;
  code: string;                   // IdentifierKK || IdentifierZPLM (для подписи в Select)
  raw?: ConstructionProjectRaw;   // оригинальная строка (на случай нужды деталей)
}

export interface SiteItem {
  id: number;
  title: string;
  constructionPermissionNumber: string;
  constructionProjectNumber: string;
  stageNumber: string;
}
