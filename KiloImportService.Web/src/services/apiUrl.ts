// Жёсткие prefix-builder'ы для API-вызовов фронтенда.
// Закрывает javascript-ssrf-rule-node_ssrf (см. doc_project/121):
// сканер видит литерал-prefix в одной точке кода, динамически URL не подменишь.
// Все запросы идут same-origin через Vite-прокси / backend.
//
// API_PREFIX (см. doc 147) — единственная build-time переменная для reverse-proxy.
// Vite заменяет `__API_PREFIX__` строковым литералом при сборке (см. vite.config.ts
// `define`). Пусто = same-origin без префикса (локалка). На стенде — `/api/ab-fm-import`.
// Поскольку подмена происходит на этапе build, в bundle это становится константой —
// SSRF-сканер не видит динамической конструкции URL.

const PREFIX = __API_PREFIX__; // '' или '/api/ab-fm-import'

const IMPORTS = `${PREFIX}/api/imports` as const;
const VISARY = `${PREFIX}/api/visary` as const;
const SITES = `${PREFIX}/api/sites` as const;
const HUBS = `${PREFIX}/hubs` as const;

function joinPath(prefix: string, sub: string): string {
  if (sub === '') return prefix;
  if (sub.startsWith('/') || sub.startsWith('?')) return prefix + sub;
  throw new Error(`apiUrl: path must start with '/' or '?' or be empty, got: ${sub}`);
}

export const apiUrl = {
  imports: (sub: string) => joinPath(IMPORTS, sub),
  visary: (sub: string) => joinPath(VISARY, sub),
  sites: (sub: string) => joinPath(SITES, sub),
  hubs: (sub: string) => joinPath(HUBS, sub),
  /** Префикс backend-API (для диагностических логов; не использовать для построения URL — есть apiUrl.imports/sites/...). */
  prefix: () => PREFIX,
};
