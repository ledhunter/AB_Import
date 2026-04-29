/**
 * Общие типы для библиотеки методов Visary ListView.
 *
 * Архитектура — двухслойная:
 *   1. Generic-ядро (этот файл + createListViewService + parseListViewResponse)
 *      знает только про общий контракт ListView API (POST /api/visary/listview/{mnemonic}).
 *   2. Адаптеры сущностей (services/listView/entities/*.ts) — конфиг и маппер
 *      под конкретную сущность Visary (проект, объект строительства, организация и т.д.).
 *
 * См. doc_project/plan-listview-library.md и doc_project/08-visary-api-integration.md.
 */

/**
 * Универсальный ответ Visary ListView API.
 * T — тип конкретной сырой строки (PascalCase, как в API).
 *
 * Реальный формат ответа Visary: `{ Data: T[], Total: number, Summaries: unknown[] }`.
 * Для устойчивости поддерживаем альтернативные имена (`Items`/`TotalCount`, camelCase) —
 * на случай если другие эндпоинты вернут их в ином виде.
 */
export interface ListViewResponseRaw<T> {
  Data?: T[];
  Items?: T[];
  items?: T[];
  Total?: number;
  TotalCount?: number;
  totalCount?: number;
  Summaries?: unknown[];
}

/**
 * Запрос-параметры, которые потребитель сервиса может варьировать на каждом вызове.
 * Не путать с конфигурацией сервиса (`ListViewServiceConfig`) — там фиксированные
 * mnemonic + columns + сортировка по умолчанию.
 */
export interface ListViewQuery {
  pageSkip?: number;
  pageSize?: number;
  searchString?: string;
  /** JSON-строка фильтра Visary (см. ExtraFilter в API). */
  extraFilter?: string | null;
  associatedId?: number | null;
  /** Переопределить сортировку, заданную в ListViewServiceConfig.defaultSort. */
  sorts?: string;
  /** AbortSignal — пробрасывается в fetch. */
  signal?: AbortSignal;
}

/**
 * Нормализованный результат ListView-запроса.
 */
export interface ListViewResult<TItem> {
  items: TItem[];
  totalCount: number;
}

/**
 * Маппер: сырая строка из Visary (PascalCase) → UI-тип (camelCase, нормализованный).
 *
 * ⚠️ В реализациях используй `||`, а не `??`, для fallback'ов:
 *    backend может вернуть пустую строку — `??` её пропустит, `||` заменит на дефолт.
 *    См. doc_project/08-visary-api-integration.md, ошибка №4.
 */
export type ListViewMapper<TRaw, TItem> = (raw: TRaw) => TItem;

/**
 * Конфигурация сервиса под конкретную сущность Visary.
 */
export interface ListViewServiceConfig<TRaw, TItem> {
  /** Mnemonic эндпоинта Visary, например 'constructionproject' или 'constructionsite'. */
  mnemonic: string;
  /** Список колонок для запроса (PascalCase, как в Visary API). */
  columns: string[];
  /** Сортировка по умолчанию: JSON-строка вида '[{"selector":"ID","desc":true}]'. */
  defaultSort?: string;
  /** Размер страницы по умолчанию (если потребитель не передал свой). */
  defaultPageSize?: number;
  /** Маппер сырой строки → UI-тип. */
  toItem: ListViewMapper<TRaw, TItem>;
  /** Тег для логов, например '[projects]'. Появляется в console.* логах сервиса. */
  logTag?: string;
}

/**
 * Публичный контракт сервиса. Адаптеры сущностей возвращают именно его.
 */
export interface ListViewService<TItem> {
  /** Mnemonic эндпоинта (на случай диагностики/логов вне сервиса). */
  readonly mnemonic: string;
  /** Тег для логов потребителя (например, useListView). */
  readonly logTag: string;
  /** Запрос. Идемпотентность/кэширование — забота уровня выше (хука). */
  fetch: (query?: ListViewQuery) => Promise<ListViewResult<TItem>>;
}
