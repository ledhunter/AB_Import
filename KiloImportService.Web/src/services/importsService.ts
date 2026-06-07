/**
 * Клиент собственного backend KiloImportService.Api.
 *
 * Все запросы идут через относительные пути — в dev их перехватывает Vite proxy
 * (см. vite.config.ts: `/api/imports`, `/api/import-types`, `/hubs/imports`).
 * В production — backend на том же origin, что и frontend.
 *
 * Никаких глобальных побочек при импорте: токены не читаются (backend пока
 * без авторизации), env читаются лениво в `getBackendBase()` для тестов под Node.
 */

import type {
  ApiImportReport,
  ApiImportSession,
  ApiImportSessionsListResponse,
  ApiImportTypesResponse,
  ApiUploadResult,
} from '../types/api';
import { apiUrl } from './apiUrl';
import { getAccessToken } from './auth';
import { devError, devGroupCollapsed, devGroupEnd, devInfo, devLog, devWarn } from './devLog';
import { safeFetch } from './safeFetch';

// ─────────────────── Errors ───────────────────

/** Ошибка взаимодействия с backend (HTTP 4xx/5xx, network). */
export class ImportsApiError extends Error {
  public readonly status: number | null;
  public readonly responseText: string | null;

  constructor(message: string, status: number | null, responseText: string | null) {
    super(message);
    this.name = 'ImportsApiError';
    this.status = status;
    this.responseText = responseText;
  }
}

// ─────────────────── Internals ───────────────────

let _requestCounter = 0;

const nextRequestId = (): string => {
  _requestCounter += 1;
  return _requestCounter.toString(36).padStart(4, '0');
};

const LOG_TAG = '[ImportsAPI]';

interface RequestOptions {
  signal?: AbortSignal;
}

/**
 * Если зарегистрирован access_token (см. services/auth.ts) — добавляем
 * Authorization-header. Иначе init остаётся без изменений (dev-режим).
 */
async function withAuth(init: RequestInit): Promise<RequestInit> {
  const token = await getAccessToken();
  if (!token) return init;
  const headers = new Headers(init.headers ?? {});
  headers.set('Authorization', `Bearer ${token}`);
  return { ...init, headers };
}

async function fetchJson<T>(
  path: string,
  init: RequestInit & RequestOptions,
): Promise<T> {
  const id = nextRequestId();
  const method = init.method ?? 'GET';
  devGroupCollapsed(`${LOG_TAG} → ${method} ${path}  #${id}`);
  if (init.body && typeof init.body === 'string') {
    devLog('request body:', init.body);
  }
  devGroupEnd();

  const start =
    typeof performance !== 'undefined' && typeof performance.now === 'function'
      ? performance.now()
      : Date.now();

  let response: Response;
  try {
    response = await safeFetch(path, await withAuth(init));
  } catch (err) {
    if (err instanceof DOMException && err.name === 'AbortError') {
      devInfo(`${LOG_TAG} ⊘ aborted ${method} ${path} #${id}`);
      throw err;
    }
    const message = err instanceof Error ? err.message : String(err);
    devError(`${LOG_TAG} ✗ NETWORK ${method} ${path} #${id} —`, message);
    throw new ImportsApiError(`Сетевая ошибка: ${message}`, null, null);
  }

  const elapsed = Math.round(
    (typeof performance !== 'undefined' && typeof performance.now === 'function'
      ? performance.now()
      : Date.now()) - start,
  );

  // 204 No Content → undefined
  if (response.status === 204) {
    devInfo(`${LOG_TAG} ← 204 ${method} ${path} #${id} (${elapsed}ms)`);
    return undefined as T;
  }

  // Пытаемся распарсить тело как JSON; если не получилось — text.
  let raw: string;
  try {
    raw = await response.text();
  } catch {
    raw = '';
  }

  if (!response.ok) {
    devError(
      `${LOG_TAG} ✗ ${response.status} ${method} ${path} #${id} (${elapsed}ms) —`,
      raw,
    );
    let serverMessage = '';
    try {
      const parsed = raw ? JSON.parse(raw) : null;
      if (parsed && typeof parsed === 'object' && 'error' in parsed) {
        serverMessage = String((parsed as Record<string, unknown>).error);
      }
    } catch {
      /* ignore */
    }
    throw new ImportsApiError(
      serverMessage || `Backend вернул ${response.status} ${response.statusText}`,
      response.status,
      raw,
    );
  }

  devInfo(`${LOG_TAG} ← ${response.status} ${method} ${path} #${id} (${elapsed}ms)`);

  if (!raw) return undefined as T;
  try {
    return JSON.parse(raw) as T;
  } catch (err) {
    const message = err instanceof Error ? err.message : String(err);
    throw new ImportsApiError(`Не удалось распарсить ответ: ${message}`, response.status, raw);
  }
}

// ─────────────────── Public API ───────────────────

export interface UploadImportPayload {
  importTypeCode: string;
  file: File;
  projectId?: number | null;
  siteId?: number | null;
  /**
   * Опциональный второй файл. Сейчас используется только Финмоделью —
   * заказчик загружает «файл с планами» (лист «План»), из которого backend
   * читает краевые квартальные значения и создаёт `fmmodel` в Visary.
   * См. doc_project/110-finmodel-plan-and-fmmodel.md.
   */
  secondaryFile?: File | null;
}

/**
 * Загрузить файл и стартовать сессию импорта (`POST /api/imports`).
 * Возвращает `sessionId` — на него подписываемся через SignalR.
 */
export async function uploadImport(
  payload: UploadImportPayload,
  options: RequestOptions = {},
): Promise<ApiUploadResult> {
  const form = new FormData();
  form.set('importTypeCode', payload.importTypeCode);
  form.set('file', payload.file);
  if (payload.projectId != null) form.set('projectId', String(payload.projectId));
  if (payload.siteId != null) form.set('siteId', String(payload.siteId));
  if (payload.secondaryFile) form.set('secondaryFile', payload.secondaryFile);

  return fetchJson<ApiUploadResult>(apiUrl.imports(''), {
    method: 'POST',
    body: form,
    signal: options.signal,
  });
}

export interface ListImportsOptions extends RequestOptions {
  skip?: number;
  take?: number;
  status?: string;
  importTypeCode?: string;
  /** ID проекта Visary для фильтрации сессий. */
  projectId?: number;
}

/**
 * Получить список сессий импорта (история) — `GET /api/imports`.
 * Отсортированы по StartedAt DESC. Фильтры по статусу/типу/проекту и пагинация — опциональны.
 */
export function listImports(
  options: ListImportsOptions = {},
): Promise<ApiImportSessionsListResponse> {
  const params = new URLSearchParams();
  if (options.skip != null) params.set('skip', String(options.skip));
  if (options.take != null) params.set('take', String(options.take));
  if (options.status) params.set('status', options.status);
  if (options.importTypeCode) params.set('importTypeCode', options.importTypeCode);
  if (options.projectId != null) params.set('projectId', String(options.projectId));
  const qs = params.toString();
  const path = apiUrl.imports(qs ? `?${qs}` : '');
  return fetchJson<ApiImportSessionsListResponse>(path, {
    method: 'GET',
    signal: options.signal,
  });
}

/** Получить состояние сессии (для polling fallback). */
export function getImportSession(
  sessionId: string,
  options: RequestOptions = {},
): Promise<ApiImportSession> {
  return fetchJson<ApiImportSession>(
    apiUrl.imports(`/${encodeURIComponent(sessionId)}`),
    { method: 'GET', signal: options.signal },
  );
}

export interface GetReportOptions extends RequestOptions {
  skip?: number;
  take?: number;
  /**
   * Имена листов, которые нужно исключить из выборки строк и из `total`. Используется
   * для клиентского сворачивания листов в UI: пагинация считается только по видимым
   * строкам. Лист с `null`-именем (одностраничные импорты) не исключается — для него
   * нет соответствующей строки в `sheetTotals`.
   */
  excludeSheets?: string[];
}

/** Получить подробный отчёт по сессии. */
export function getImportReport(
  sessionId: string,
  options: GetReportOptions = {},
): Promise<ApiImportReport> {
  const params = new URLSearchParams();
  if (options.skip != null) params.set('skip', String(options.skip));
  if (options.take != null) params.set('take', String(options.take));
  // ASP.NET связывает повторяющиеся query-параметры в string[] — добавляем каждый отдельно.
  for (const sheet of options.excludeSheets ?? []) {
    if (sheet) params.append('excludeSheets', sheet);
  }
  const qs = params.toString();
  const path = apiUrl.imports(`/${encodeURIComponent(sessionId)}/report${qs ? `?${qs}` : ''}`);
  return fetchJson<ApiImportReport>(path, { method: 'GET', signal: options.signal });
}

/** Применить валидированные строки в visary_db (`POST /api/imports/{id}/apply`). */
export function applyImport(
  sessionId: string,
  options: RequestOptions = {},
): Promise<{ sessionId: string; status: string }> {
  return fetchJson(apiUrl.imports(`/${encodeURIComponent(sessionId)}/apply`), {
    method: 'POST',
    signal: options.signal,
  });
}

/** Отменить сессию (только до Apply). */
export function cancelImport(
  sessionId: string,
  options: RequestOptions = {},
): Promise<{ sessionId: string; status: string }> {
  return fetchJson(apiUrl.imports(`/${encodeURIComponent(sessionId)}/cancel`), {
    method: 'POST',
    signal: options.signal,
  });
}

/**
 * Сгенерировать PDF-отчёт по списку сессий — `POST /api/imports/export-pdf`.
 * Возвращает `Blob` с `application/pdf` для скачивания.
 *
 * NB: fetchJson здесь не используем — он парсит ответ как JSON; нам нужен бинарный blob.
 */
export async function exportImportsPdf(
  sessionIds: string[],
  options: RequestOptions = {},
): Promise<Blob> {
  if (sessionIds.length === 0) {
    throw new Error('Не выбрано ни одной сессии для выгрузки.');
  }

  const id = nextRequestId();
  const path = apiUrl.imports('/export-pdf');
  devInfo(`${LOG_TAG} → POST ${path}  #${id}  sessions=${sessionIds.length}`);
  const start =
    typeof performance !== 'undefined' && typeof performance.now === 'function'
      ? performance.now()
      : Date.now();

  let response: Response;
  try {
    response = await safeFetch(path, await withAuth({
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ sessionIds }),
      signal: options.signal,
    }));
  } catch (err) {
    if (err instanceof DOMException && err.name === 'AbortError') throw err;
    const message = err instanceof Error ? err.message : String(err);
    throw new ImportsApiError(`Сетевая ошибка: ${message}`, null, null);
  }

  const elapsed = Math.round(
    (typeof performance !== 'undefined' && typeof performance.now === 'function'
      ? performance.now()
      : Date.now()) - start,
  );

  if (!response.ok) {
    const text = await response.text().catch(() => '');
    let serverMessage = '';
    try {
      const parsed = text ? JSON.parse(text) : null;
      if (parsed && typeof parsed === 'object' && 'error' in parsed) {
        serverMessage = String((parsed as Record<string, unknown>).error);
      }
    } catch {
      /* ignore */
    }
    devError(`${LOG_TAG} ✗ ${response.status} POST ${path} #${id} (${elapsed}ms) —`, text);
    throw new ImportsApiError(
      serverMessage || `Backend вернул ${response.status} ${response.statusText}`,
      response.status,
      text,
    );
  }

  const blob = await response.blob();
  devInfo(`${LOG_TAG} ← ${response.status} POST ${path} #${id} (${elapsed}ms)  bytes=${blob.size}`);
  return blob;
}

/** Получить реестр поддерживаемых типов импорта. */
export async function getImportTypes(
  options: RequestOptions = {},
): Promise<ApiImportTypesResponse> {
  try {
    return await fetchJson<ApiImportTypesResponse>('/api/import-types', {
      method: 'GET',
      signal: options.signal,
    });
  } catch {
    // Fallback mock если backend недоступен
    devWarn('[ImportsAPI] Backend недоступен, используем mock типов импорта');
    return {
      items: [
        { id: 'rooms', label: 'Помещения', description: 'Импорт реестра помещений', isImplemented: true },
        { id: 'finmodel', label: 'Финмодель', description: 'Импорт финансовой модели', isImplemented: true },
      ],
      total: 2,
    };
  }
}
