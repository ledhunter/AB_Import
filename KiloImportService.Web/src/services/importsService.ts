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
  ApiImportTypesResponse,
  ApiUploadResult,
} from '../types/api';

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

async function fetchJson<T>(
  path: string,
  init: RequestInit & RequestOptions,
): Promise<T> {
  const id = nextRequestId();
  const method = init.method ?? 'GET';
  console.groupCollapsed(`${LOG_TAG} → ${method} ${path}  #${id}`);
  if (init.body && typeof init.body === 'string') {
    console.log('request body:', init.body);
  }
  console.groupEnd();

  const start =
    typeof performance !== 'undefined' && typeof performance.now === 'function'
      ? performance.now()
      : Date.now();

  let response: Response;
  try {
    response = await fetch(path, init);
  } catch (err) {
    if (err instanceof DOMException && err.name === 'AbortError') {
      console.info(`${LOG_TAG} ⊘ aborted ${method} ${path} #${id}`);
      throw err;
    }
    const message = err instanceof Error ? err.message : String(err);
    console.error(`${LOG_TAG} ✗ NETWORK ${method} ${path} #${id} —`, message);
    throw new ImportsApiError(`Сетевая ошибка: ${message}`, null, null);
  }

  const elapsed = Math.round(
    (typeof performance !== 'undefined' && typeof performance.now === 'function'
      ? performance.now()
      : Date.now()) - start,
  );

  // 204 No Content → undefined
  if (response.status === 204) {
    console.info(`${LOG_TAG} ← 204 ${method} ${path} #${id} (${elapsed}ms)`);
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
    console.error(
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

  console.info(`${LOG_TAG} ← ${response.status} ${method} ${path} #${id} (${elapsed}ms)`);

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

  return fetchJson<ApiUploadResult>('/api/imports', {
    method: 'POST',
    body: form,
    signal: options.signal,
  });
}

/** Получить состояние сессии (для polling fallback). */
export function getImportSession(
  sessionId: string,
  options: RequestOptions = {},
): Promise<ApiImportSession> {
  return fetchJson<ApiImportSession>(
    `/api/imports/${encodeURIComponent(sessionId)}`,
    { method: 'GET', signal: options.signal },
  );
}

export interface GetReportOptions extends RequestOptions {
  skip?: number;
  take?: number;
}

/** Получить подробный отчёт по сессии. */
export function getImportReport(
  sessionId: string,
  options: GetReportOptions = {},
): Promise<ApiImportReport> {
  const params = new URLSearchParams();
  if (options.skip != null) params.set('skip', String(options.skip));
  if (options.take != null) params.set('take', String(options.take));
  const qs = params.toString();
  const path = `/api/imports/${encodeURIComponent(sessionId)}/report${qs ? `?${qs}` : ''}`;
  return fetchJson<ApiImportReport>(path, { method: 'GET', signal: options.signal });
}

/** Применить валидированные строки в visary_db (`POST /api/imports/{id}/apply`). */
export function applyImport(
  sessionId: string,
  options: RequestOptions = {},
): Promise<{ sessionId: string; status: string }> {
  return fetchJson(`/api/imports/${encodeURIComponent(sessionId)}/apply`, {
    method: 'POST',
    signal: options.signal,
  });
}

/** Отменить сессию (только до Apply). */
export function cancelImport(
  sessionId: string,
  options: RequestOptions = {},
): Promise<{ sessionId: string; status: string }> {
  return fetchJson(`/api/imports/${encodeURIComponent(sessionId)}/cancel`, {
    method: 'POST',
    signal: options.signal,
  });
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
  } catch (err) {
    // Fallback mock если backend недоступен
    console.warn('[ImportsAPI] Backend недоступен, используем mock типов импорта');
    return {
      items: [
        { id: 'rooms', label: 'Помещения', description: 'Импорт реестра помещений', isImplemented: true },
        { id: 'finmodel', label: 'Финмодель', description: 'Импорт финансовой модели', isImplemented: true },
      ],
      total: 2,
    };
  }
}
