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
  address: string;
  constructionProjectNumber: string;
  type: string;
  totalArea: number | null;
  raw?: ConstructionSiteRaw;
}

/**
 * Сырая строка объекта строительства из Visary ListView (mnemonic `constructionsite`).
 * Имена полей соответствуют запрашиваемым `Columns` (PascalCase).
 */
export interface ConstructionSiteRaw {
  ID: number;
  Date?: string | null;
  Project?: string | null;
  Title?: string | null;
  Location?: string | null;
  ConstructionProjectNumber?: string | null;
  Address?: string | null;
  Type?: string | null;
  EstateClass?: string | null;
  BuildingMaterial?: string | null;
  FinishingMaterial?: string | null;
  TotalArea?: number | null;
  StartDate?: string | null;
  FinishDate?: string | null;
  MonthDuration?: number | null;
  TempOfConstruction?: string | null;
  ClaimedCost?: number | null;
  AreaCost?: number | null;
  RiskFund?: number | null;
  Borrower?: string | null;
  ComplexID?: number | null;
  Town?: string | null;
  Comment?: string | null;
  RowVersion?: string | number | null;
}
