/**
 * Тонкая обёртка над fetch для запросов к Visary API.
 *
 * - Префикс пути `/api/visary/...` проксируется Vite в `VITE_VISARY_API_URL` (см. vite.config.ts).
 * - Bearer-токен берётся из `VITE_VISARY_API_TOKEN` (.env.local).
 * - На 401 кидает `VisaryAuthError` — UI показывает сообщение «Токен истёк, обнови .env.local».
 */

import { apiUrl } from './apiUrl';
import { devError, devGroupCollapsed, devGroupEnd, devInfo, devWarn } from './devLog';
import { safeFetch } from './safeFetch';

/**
 * Ленивое чтение токена — позволяет тестам импортировать модуль вне Vite.
 */
function getToken(): string {
  // import.meta.env существует только в среде Vite (browser/dev-server).
  const env = (import.meta as { env?: Record<string, string | undefined> }).env;
  return env?.VITE_VISARY_API_TOKEN ?? '';
}

export class VisaryApiError extends Error {
  readonly status: number;
  readonly body?: unknown;

  constructor(message: string, status: number, body?: unknown) {
    super(message);
    this.name = 'VisaryApiError';
    this.status = status;
    this.body = body;
  }
}

export class VisaryAuthError extends VisaryApiError {
  constructor(body?: unknown) {
    super(
      'Bearer-токен Visary истёк или невалиден. Обнови VITE_VISARY_API_TOKEN в .env.local и перезапусти dev-сервер.',
      401,
      body,
    );
    this.name = 'VisaryAuthError';
  }
}

interface RequestOptions {
  signal?: AbortSignal;
  queryParams?: Record<string, string | number>;
}

/**
 * Сокращённое представление токена для логов: `eyJ...Pvg` (12 первых + 8 последних).
 */
function maskToken(token: string): string {
  if (token.length <= 24) return '***';
  return `${token.slice(0, 12)}...${token.slice(-8)} (len=${token.length})`;
}

function buildUrl(path: string, queryParams?: Record<string, string | number>): string {
  const base = apiUrl.visary(path);
  if (!queryParams) return base;
  const params = new URLSearchParams();
  Object.entries(queryParams).forEach(([key, value]) => {
    params.append(key, String(value));
  });
  const qs = params.toString();
  return qs ? `${base}?${qs}` : base;
}

/**
 * Базовый POST-запрос к Visary API.
 * @param path — путь относительно `/api/visary` (например, `/listview/constructionproject`)
 * @param body — JSON-тело запроса
 * @param options — опции запроса (signal, queryParams)
 */
export async function visaryPost<TResponse>(
  path: string,
  body: unknown,
  options: RequestOptions = {},
): Promise<TResponse> {
  const token = getToken();
  if (!token) {
    // Намеренно не через devError: разработческая ошибка конфига — её нужно видеть и в prod-preview.
    console.error('[VisaryAPI] ❌ VITE_VISARY_API_TOKEN не задан');
    throw new VisaryApiError(
      'VITE_VISARY_API_TOKEN не задан. Создай .env.local на основе .env.example.',
      0,
    );
  }

  const url = buildUrl(path, options.queryParams);

  const requestId = Math.random().toString(36).slice(2, 8);
  const startedAt = performance.now();

  devGroupCollapsed(`[VisaryAPI] → POST ${url}  #${requestId}`);
  devInfo('  token:', maskToken(token));
  devInfo('  request body:', body);
  devGroupEnd();

  let response: Response;
  try {
    response = await safeFetch(url, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        Authorization: `Bearer ${token}`,
      },
      body: JSON.stringify(body),
      signal: options.signal,
    });
  } catch (err) {
    if (err instanceof Error && err.name === 'AbortError') {
      devWarn(`[VisaryAPI] ⊘ #${requestId} aborted`);
      throw err;
    }
    const ms = Math.round(performance.now() - startedAt);
    devError(`[VisaryAPI] ✗ NETWORK ERROR ${url} #${requestId} (${ms}ms)`, err);
    throw new VisaryApiError(
      `Сетевая ошибка при запросе ${url}: ${err instanceof Error ? err.message : String(err)}`,
      0,
    );
  }

  const ms = Math.round(performance.now() - startedAt);

  if (response.status === 401 || response.status === 403) {
    const errBody = await safeReadBody(response);
    devError(
      `[VisaryAPI] ✗ ${response.status} ${response.statusText} ${url} #${requestId} (${ms}ms)`,
      errBody,
    );
    throw new VisaryAuthError(errBody);
  }

  if (!response.ok) {
    const errBody = await safeReadBody(response);
    devError(
      `[VisaryAPI] ✗ ${response.status} ${response.statusText} ${url} #${requestId} (${ms}ms)`,
      errBody,
    );
    throw new VisaryApiError(
      `Visary API вернул ${response.status} ${response.statusText} для ${url}`,
      response.status,
      errBody,
    );
  }

  const data = (await response.json()) as TResponse;
  devGroupCollapsed(
    `[VisaryAPI] ← ${response.status} ${url} #${requestId} (${ms}ms)`,
  );
  devInfo('  response:', data);
  devGroupEnd();
  return data;
}

/**
 * GET-запрос к Visary API.
 * @param path — путь относительно `/api/visary` (например, `/crud/constructionsite/123`)
 * @param options — опции запроса (signal, queryParams)
 */
export async function visaryGet<TResponse>(
  path: string,
  options: RequestOptions = {},
): Promise<TResponse> {
  const token = getToken();
  if (!token) {
    console.error('[VisaryAPI] ❌ VITE_VISARY_API_TOKEN не задан');
    throw new VisaryApiError(
      'VITE_VISARY_API_TOKEN не задан. Создай .env.local на основе .env.example.',
      0,
    );
  }

  const url = buildUrl(path, options.queryParams);

  const requestId = Math.random().toString(36).slice(2, 8);
  const startedAt = performance.now();

  devInfo(`[VisaryAPI] → GET ${url}  #${requestId}`);

  let response: Response;
  try {
    response = await safeFetch(url, {
      method: 'GET',
      headers: {
        Authorization: `Bearer ${token}`,
      },
      signal: options.signal,
    });
  } catch (err) {
    if (err instanceof Error && err.name === 'AbortError') {
      devWarn(`[VisaryAPI] ⊘ #${requestId} aborted`);
      throw err;
    }
    const ms = Math.round(performance.now() - startedAt);
    devError(`[VisaryAPI] ✗ NETWORK ERROR ${url} #${requestId} (${ms}ms)`, err);
    throw new VisaryApiError(
      `Сетевая ошибка при запросе ${url}: ${err instanceof Error ? err.message : String(err)}`,
      0,
    );
  }

  const ms = Math.round(performance.now() - startedAt);

  if (response.status === 401 || response.status === 403) {
    const errBody = await safeReadBody(response);
    devError(
      `[VisaryAPI] ✗ ${response.status} ${response.statusText} ${url} #${requestId} (${ms}ms)`,
      errBody,
    );
    throw new VisaryAuthError(errBody);
  }

  if (!response.ok) {
    const errBody = await safeReadBody(response);
    devError(
      `[VisaryAPI] ✗ ${response.status} ${response.statusText} ${url} #${requestId} (${ms}ms)`,
      errBody,
    );
    throw new VisaryApiError(
      `Visary API вернул ${response.status} ${response.statusText} для ${url}`,
      response.status,
      errBody,
    );
  }

  const data = (await response.json()) as TResponse;
  devInfo(`[VisaryAPI] ← ${response.status} ${url} #${requestId} (${ms}ms)`);
  return data;
}

/**
 * PATCH-запрос к Visary API.
 * @param path — путь относительно `/api/visary` (например, `/crud/constructionsite/123`)
 * @param body — JSON-тело запроса
 * @param options — опции запроса (signal, queryParams)
 */
export async function visaryPatch<TResponse>(
  path: string,
  body: unknown,
  options: RequestOptions = {},
): Promise<TResponse> {
  const token = getToken();
  if (!token) {
    console.error('[VisaryAPI] ❌ VITE_VISARY_API_TOKEN не задан');
    throw new VisaryApiError(
      'VITE_VISARY_API_TOKEN не задан. Создай .env.local на основе .env.example.',
      0,
    );
  }

  const url = buildUrl(path, options.queryParams);

  const requestId = Math.random().toString(36).slice(2, 8);
  const startedAt = performance.now();

  devGroupCollapsed(`[VisaryAPI] → PATCH ${url}  #${requestId}`);
  devInfo('  token:', maskToken(token));
  devInfo('  request body:', body);
  devGroupEnd();

  let response: Response;
  try {
    response = await safeFetch(url, {
      method: 'PATCH',
      headers: {
        'Content-Type': 'application/json',
        Authorization: `Bearer ${token}`,
      },
      body: JSON.stringify(body),
      signal: options.signal,
    });
  } catch (err) {
    if (err instanceof Error && err.name === 'AbortError') {
      devWarn(`[VisaryAPI] ⊘ #${requestId} aborted`);
      throw err;
    }
    const ms = Math.round(performance.now() - startedAt);
    devError(`[VisaryAPI] ✗ NETWORK ERROR ${url} #${requestId} (${ms}ms)`, err);
    throw new VisaryApiError(
      `Сетевая ошибка при запросе ${url}: ${err instanceof Error ? err.message : String(err)}`,
      0,
    );
  }

  const ms = Math.round(performance.now() - startedAt);

  if (response.status === 401 || response.status === 403) {
    const errBody = await safeReadBody(response);
    devError(
      `[VisaryAPI] ✗ ${response.status} ${response.statusText} ${url} #${requestId} (${ms}ms)`,
      errBody,
    );
    throw new VisaryAuthError(errBody);
  }

  if (!response.ok) {
    const errBody = await safeReadBody(response);
    devError(
      `[VisaryAPI] ✗ ${response.status} ${response.statusText} ${url} #${requestId} (${ms}ms)`,
      errBody,
    );
    throw new VisaryApiError(
      `Visary API вернул ${response.status} ${response.statusText} для ${url}`,
      response.status,
      errBody,
    );
  }

  const data = (await response.json()) as TResponse;
  devGroupCollapsed(
    `[VisaryAPI] ← ${response.status} ${url} #${requestId} (${ms}ms)`,
  );
  devInfo('  response:', data);
  devGroupEnd();
  return data;
}

async function safeReadBody(response: Response): Promise<unknown> {
  try {
    const text = await response.text();
    try {
      return JSON.parse(text);
    } catch {
      return text;
    }
  } catch {
    return undefined;
  }
}
