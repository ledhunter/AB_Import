// =====================================================================
// Visary ListView API (POST /api/visary/listview/{mnemonic})
// =====================================================================

/**
 * @deprecated Используй generic-типы из `services/listView/types.ts`.
 * Сохранено только для обратной совместимости старых импортов.
 */
export interface ListViewRequest {
  Mnemonic: string;
  PageSkip: number;
  PageSize: number;
  Columns: string[];
  Sorts: string;
  Hidden: boolean;
  ExtraFilter: string | null;
  SearchString: string;
  AssociatedID: number | null;
}

/**
 * @deprecated Используй `ListViewResponseRaw<T>` из `services/listView/types.ts`.
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
  raw?: ConstructionSiteRaw;
}

/**
 * Сырая строка объекта строительства из Visary ListView (mnemonic `constructionsite`).
 * Имена полей соответствуют запрошенным `Columns` (PascalCase).
 *
 * ⚠️ Список колонок предварительный — уточнить при первом реальном запросе
 * к Visary (как было с проектами: имена ключей ответа подтверждаются логами).
 */
export interface ConstructionSiteRaw {
  ID: number;
  Title?: string | null;
  ConstructionPermissionNumber?: string | null;
  ConstructionProjectNumber?: string | null;
  StageNumber?: string | null;
  /** FK на проект строительства — для фильтрации по выбранному проекту. */
  ConstructionProjectID?: number | null;
}
