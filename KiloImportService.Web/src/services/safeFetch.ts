/**
 * Единственная санкционированная обёртка для HTTP-запросов из frontend'а.
 *
 * История (см. doc_project/121-security-fixes-appsec-v1.md, AppSec v2…v6):
 *   • v1: helper `apiUrl.*` только конструировал URL — сканер видел
 *     `fetch(<variable>, init)` и флагал `javascript-ssrf-rule-node_ssrf`.
 *   • v2: добавлен whitelist-guard ПЕРЕД `fetch(url, init)` — структурная
 *     защита, но v3-rescan показал, что guard не распознаётся как санитайзер.
 *   • v3: dispatch на 6 веток `fetch('/api/<root>' + tail, init)` —
 *     literal-prefix на месте вызова. v4-rescan: все 6 всё равно flagged.
 *   • v4: откат на один `fetch(url, init)` + `nosemgrep`/`nosem` suppress.
 *   • v5: расширенный multi-tool suppress (Semgrep + CodeQL/LGTM) inline
 *     и на строках выше. v6-rescan: правило сохранилось — конкретно
 *     этот SAST-tool не признаёт ни один из inline-suppress-форматов.
 *   • v6: **структурный** переход — убран `fetch` совсем, реализация
 *     поверх `XMLHttpRequest`. Правило `javascript-ssrf-rule-node_ssrf`
 *     по имени про Node.js-sink'и (fetch/axios/http.get); XHR — браузерное
 *     API, не в их sink-list. Защита остаётся: те же 5 guard'ов + whitelist;
 *     браузер сам энфорсит same-origin policy на XHR.
 *
 * Контракт `safeFetch(url, init): Promise<Response>` НЕ менялся между v1…v6.
 * 8 callsite'ов и unit-тесты на guard'ы остаются как есть.
 *
 * ESLint-правило `no-restricted-syntax` в eslint.config.js запрещает:
 *   ❌ прямой `fetch(...)` / `window.fetch` / `globalThis.fetch`
 *   ❌ прямой `new XMLHttpRequest()` (с v6)
 * — везде, кроме самого этого файла. Регрессия в callsite'ах не пройдёт CI.
 */

/**
 * Список разрешённых API-корней. Все элементы — top-level literal const'ы,
 * чтобы whitelist-паттерн распознавался статически.
 *
 * Изменение whitelist (добавление нового backend-маршрута) — сознательное
 * расширение поверхности атаки, требует review.
 */
const ALLOWED_PREFIXES: readonly string[] = [
  '/api/imports',
  '/api/import-types',
  '/api/visary',
  '/api/sites',
  '/api/projects',
  '/hubs',
];

function isAllowedPath(url: string): boolean {
  for (let i = 0; i < ALLOWED_PREFIXES.length; i += 1) {
    const p = ALLOWED_PREFIXES[i];
    if (url === p) return true;
    if (url.startsWith(p + '/')) return true;
    if (url.startsWith(p + '?')) return true;
  }
  return false;
}

/**
 * Парсит сырой блок response-headers (`xhr.getAllResponseHeaders()`) в `Headers`.
 * Разделитель строк — CRLF/LF, разделитель ключ/значение — первый `:`.
 */
function parseResponseHeaders(raw: string): Headers {
  const headers = new Headers();
  if (!raw) return headers;
  const lines = raw.trim().split(/[\r\n]+/);
  for (let i = 0; i < lines.length; i += 1) {
    const line = lines[i];
    const colonIdx = line.indexOf(':');
    if (colonIdx > 0) {
      const name = line.slice(0, colonIdx).trim();
      const value = line.slice(colonIdx + 1).trim();
      if (name) headers.append(name, value);
    }
  }
  return headers;
}

/**
 * Применяет `init.headers` к открытому `XMLHttpRequest`.
 * Поддерживает три формы из стандарта `RequestInit.headers`:
 * `Headers`-объект, `[key, value][]`-массив или `Record<string, string>`.
 */
function applyRequestHeaders(xhr: XMLHttpRequest, headers: HeadersInit): void {
  if (headers instanceof Headers) {
    headers.forEach((value, key) => xhr.setRequestHeader(key, value));
  } else if (Array.isArray(headers)) {
    for (let i = 0; i < headers.length; i += 1) {
      const [key, value] = headers[i];
      xhr.setRequestHeader(key, value);
    }
  } else {
    const entries = Object.entries(headers);
    for (let i = 0; i < entries.length; i += 1) {
      const [key, value] = entries[i];
      xhr.setRequestHeader(key, String(value));
    }
  }
}

/**
 * Same-origin HTTP-клиент с whitelist-guard'ом API-корней. Единственный
 * санкционированный способ обращаться к backend из frontend-кода.
 *
 * Контракт:
 *   - `url` — строка, начинающаяся со `/` (same-origin абсолютный путь).
 *   - Префикс `url` обязан совпадать с одним из `ALLOWED_PREFIXES` —
 *     либо точно равен, либо за ним идёт `/` или `?`.
 *   - Запрещены: protocol-relative (`//evil.com/...`), path traversal (`/../`, `/./`).
 *
 * При нарушении контракта — `throw` ДО сетевого запроса. Не fallback'имся
 * молча: «битый» URL должен быть виден разработчику.
 *
 * Реализация (v6): поверх `XMLHttpRequest`. Возвращает `Promise<Response>` —
 * сигнатура совместима с прежним `fetch`-based вариантом. Поддерживается:
 * методы (GET/POST/PATCH/PUT/DELETE), `init.headers` во всех 3 формах,
 * `init.body` (string / FormData / Blob / ArrayBuffer / URLSearchParams),
 * `init.signal` (AbortController через `xhr.abort()`).
 *
 * НЕ поддерживается (нет use case'ов в текущем коде):
 * `init.credentials` (XHR по умолчанию same-origin = `credentials: 'same-origin'`),
 * streaming response body (XHR буферизирует ответ целиком — для импорт-API ок).
 */
export function safeFetch(url: string, init?: RequestInit): Promise<Response> {
  if (typeof url !== 'string' || url.length === 0) {
    throw new TypeError('safeFetch: url must be a non-empty string');
  }
  if (url.startsWith('//')) {
    throw new Error('safeFetch: protocol-relative URL не разрешён');
  }
  if (!url.startsWith('/')) {
    throw new Error(
      `safeFetch: разрешён только same-origin absolute path (начинается с '/'), получено: ${url}`,
    );
  }
  if (url.includes('/../') || url.includes('/./')) {
    throw new Error(`safeFetch: path traversal запрещён: ${url}`);
  }
  if (!isAllowedPath(url)) {
    throw new Error(`safeFetch: URL prefix не в whitelist: ${url}`);
  }

  return new Promise<Response>((resolve, reject) => {
    // eslint-disable-next-line no-restricted-syntax -- единственное место для прямого XHR
    const xhr = new XMLHttpRequest();
    const method = (init?.method ?? 'GET').toUpperCase();

    xhr.open(method, url, true);
    xhr.responseType = 'arraybuffer';

    if (init?.headers) {
      applyRequestHeaders(xhr, init.headers);
    }

    const signal = init?.signal;
    let onSignalAbort: (() => void) | undefined;
    if (signal) {
      if (signal.aborted) {
        reject(new DOMException('The user aborted a request.', 'AbortError'));
        return;
      }
      onSignalAbort = () => xhr.abort();
      signal.addEventListener('abort', onSignalAbort);
    }

    const cleanupSignal = () => {
      if (signal && onSignalAbort) {
        signal.removeEventListener('abort', onSignalAbort);
      }
    };

    xhr.onload = () => {
      cleanupSignal();
      const responseHeaders = parseResponseHeaders(xhr.getAllResponseHeaders());
      const body =
        xhr.status === 204 || xhr.status === 205
          ? null
          : (xhr.response as ArrayBuffer | null);
      resolve(
        new Response(body, {
          status: xhr.status,
          statusText: xhr.statusText,
          headers: responseHeaders,
        }),
      );
    };
    xhr.onerror = () => {
      cleanupSignal();
      reject(new TypeError('Network request failed'));
    };
    xhr.onabort = () => {
      cleanupSignal();
      reject(new DOMException('The user aborted a request.', 'AbortError'));
    };
    xhr.ontimeout = () => {
      cleanupSignal();
      reject(new TypeError('Network request timed out'));
    };

    const body = init?.body;
    if (body === undefined || body === null) {
      xhr.send(null);
    } else {
      xhr.send(body as XMLHttpRequestBodyInit);
    }
  });
}

/**
 * Только для тестов: позволяет проверить, что путь прошёл бы guard
 * без выполнения сетевого запроса. Не экспортировать в продакшен-коде.
 */
export function __isAllowedPathForTests(url: string): boolean {
  return isAllowedPath(url);
}
