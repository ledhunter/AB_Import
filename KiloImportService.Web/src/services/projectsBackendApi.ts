/**
 * Клиент собственного backend'а KiloImportService.Api для проектов.
 *
 * Контракт:
 *   POST /api/projects/sync       — backend синхронизирует все проекты из Visary в свою БД.
 *   GET  /api/projects/search?q=  — поиск с fallback в Visary при пустом локальном результате.
 *
 * См. doc_project/18-projects-cache.md.
 */

import type { ProjectItem } from '../types/listView';

export interface BackendProjectDto {
  id: number;
  title: string;
}

export interface ProjectsSearchResponse {
  items: BackendProjectDto[];
  fromFallback: boolean;
  total: number;
}

export interface ProjectsSyncResponse {
  total: number;
  upserted: number;
  durationMs: number;
}

const SEARCH_PATH = '/api/projects/search';
const SYNC_PATH = '/api/projects/sync';

/**
 * Синхронизировать кэш проектов: backend дернёт Visary ListView постранично
 * и upsert'ит в локальную БД. Идемпотентно — можно вызывать при каждом open формы.
 */
export async function syncProjects(signal?: AbortSignal): Promise<ProjectsSyncResponse> {
  const startedAt = performance.now();
  console.info('[projectsBackendApi] → POST /api/projects/sync');
  const response = await fetch(SYNC_PATH, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    signal,
  });
  if (!response.ok) {
    const body = await safeBody(response);
    throw new Error(`Backend sync вернул ${response.status}: ${body}`);
  }
  const data = (await response.json()) as ProjectsSyncResponse;
  console.info(
    `[projectsBackendApi] ← 200 sync (${Math.round(performance.now() - startedAt)}ms): total=${data.total} upserted=${data.upserted}`,
  );
  return data;
}

/**
 * Поиск проектов через backend. Пустой `q` → возвращает первую страницу локальной БД.
 * Если backend не нашёл локально — он сам сделает fallback-запрос в Visary,
 * upsert'ит результат и вернёт его (поле `fromFallback=true`).
 */
export async function searchProjects(
  query: string,
  options: { limit?: number; signal?: AbortSignal } = {},
): Promise<ProjectsSearchResponse> {
  const { limit = 50, signal } = options;
  const url = new URL(SEARCH_PATH, window.location.origin);
  if (query) url.searchParams.set('q', query);
  url.searchParams.set('limit', String(limit));

  const startedAt = performance.now();
  console.info(`[projectsBackendApi] → GET ${url.pathname}${url.search}`);
  const response = await fetch(url.pathname + url.search, { signal });
  if (!response.ok) {
    const body = await safeBody(response);
    throw new Error(`Backend search вернул ${response.status}: ${body}`);
  }
  const data = (await response.json()) as ProjectsSearchResponse;
  console.info(
    `[projectsBackendApi] ← 200 search q='${query}' (${Math.round(
      performance.now() - startedAt,
    )}ms): items=${data.items.length} fromFallback=${data.fromFallback}`,
  );
  return data;
}

/** Маппинг backend DTO → UI-тип. */
export function toProjectItem(dto: BackendProjectDto): ProjectItem {
  return { id: dto.id, title: dto.title };
}

async function safeBody(r: Response): Promise<string> {
  try {
    return await r.text();
  } catch {
    return '';
  }
}
