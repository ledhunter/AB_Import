// Жёсткие prefix-builder'ы для API-вызовов фронтенда.
// Закрывает javascript-ssrf-rule-node_ssrf (см. doc_project/121):
// сканер видит литерал-prefix в одной точке кода, динамически URL не подменишь.
// Все запросы идут same-origin через Vite-прокси / backend.

const IMPORTS = '/api/imports' as const;
const VISARY = '/api/visary' as const;
const SITES = '/api/sites' as const;
const HUBS = '/hubs' as const;

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
};
