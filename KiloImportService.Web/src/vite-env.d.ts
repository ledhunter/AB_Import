/// <reference types="vite/client" />

// Build-time константы, инжектится через `define` в vite.config.ts (см. doc 147).
// __API_PREFIX__ — URL-префикс backend-API (`/api/ab-fm-import` или пусто для same-origin).
// __BUILD_TIME__ — ISO timestamp сборки bundle, выводится в boot-логе фронтенда.
declare const __API_PREFIX__: string;
declare const __BUILD_TIME__: string;
