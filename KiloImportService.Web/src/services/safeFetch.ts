/**
 * Единственная санкционированная обёртка над глобальным `fetch` для frontend'а.
 *
 * Зачем (см. doc_project/121-security-fixes-appsec-v1.md, AppSec v2-rescan):
 * предыдущий помощник `apiUrl.*` только конструировал URL — сканер видел
 * `fetch(<variable>, init)` без literal-префикса на месте вызова и продолжал
 * флагать `javascript-ssrf-rule-node_ssrf`. `safeFetch` добавляет whitelist API-корней
 * в точке вызова `fetch` — это и рекомендуют формулировкой «введите белый список
 * разрешённых ресурсов», и одновременно даёт структурную защиту: невозможно
 * нечаянно отправить запрос на чужой origin или мимо известного backend-маршрута.
 *
 * Анти-паттерны, которые этот файл закрывает (см. doc 121 § «Анти-паттерны»):
 *   ❌ `fetch(\`/api/.../${userId}\`, init)`               — литерал в template literal
 *   ❌ `fetch(apiUrl.imports(sub), init)`                   — обёртка-конструктор без guard
 *   ❌ `fetch(\`${env.API_HOST}/api/...\`, init)`           — конкатенация с env/state
 * Правильно:
 *   ✅ `safeFetch(apiUrl.imports(sub), init)`               — конструктор + whitelist-guard
 *   ✅ `safeFetch('/api/imports/sync', init)`               — литерал, тоже проходит guard
 *
 * ESLint-правило `no-restricted-syntax` в eslint.config.js запрещает прямой
 * вызов глобального `fetch` где угодно кроме самого этого файла — попытка
 * вернуть «голый» `fetch(...)` в код упадёт на `npm run lint` / CI.
 */

/**
 * Список разрешённых API-корней. Все элементы — top-level literal const'ы,
 * чтобы сканер мог статически развернуть значение и распознать whitelist-паттерн.
 *
 * Изменение whitelist (добавление нового backend-маршрута) — сознательное
 * расширение поверхности атаки SSRF, требует review.
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
 * `fetch` с guard'ом по whitelist API-корней. Единственный санкционированный
 * способ обращаться к backend из frontend-кода.
 *
 * Контракт:
 *   - `url` — строка, начинающаяся со `/` (same-origin absolute path).
 *   - Префикс `url` обязан совпадать с одним из `ALLOWED_PREFIXES` —
 *     либо точно равен, либо за ним идёт `/` или `?`.
 *   - Запрещены: protocol-relative (`//evil.com/...`), path traversal (`/../`, `/./`).
 *
 * При нарушении контракта — `throw` ДО сетевого запроса. Не fallback'имся
 * на `fetch(url)` молча: «битый» URL должен быть виден разработчику.
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
  // eslint-disable-next-line no-restricted-syntax -- единственное место, где разрешён прямой fetch
  return fetch(url, init);
}

/**
 * Только для тестов: позволяет проверить, что путь прошёл бы guard
 * без выполнения сетевого запроса. Не экспортировать в продакшен-коде.
 */
export function __isAllowedPathForTests(url: string): boolean {
  return isAllowedPath(url);
}
