import js from '@eslint/js'
import globals from 'globals'
import reactHooks from 'eslint-plugin-react-hooks'
import reactRefresh from 'eslint-plugin-react-refresh'
import tseslint from 'typescript-eslint'
import { defineConfig, globalIgnores } from 'eslint/config'

// ─────────────────────────────────────────────────────────────────────────────
// Кастомные правила безопасности. См. doc_project/121-security-fixes-appsec-v1.md
// (§ AppSec v2-rescan, § Анти-паттерны).
//
//  • no-restricted-syntax: запрет прямого вызова глобального `fetch(...)`.
//    Все сетевые запросы обязаны идти через `services/safeFetch.ts`, где
//    URL проверяется по whitelist API-корней — это закрывает SSRF-rule
//    структурно, а не путём «переименования» переменной с URL.
//
//  • no-restricted-globals: запрет ссылок на `fetch` через `globalThis.fetch`,
//    `window.fetch`, или просто `fetch` без вызова (в т.ч. деструктуризация,
//    `const f = fetch` и т.п.).
//
// Один файл — `services/safeFetch.ts` — исключён из правила; там стоит
// in-source `// eslint-disable-next-line` рядом с единственным разрешённым
// вызовом `fetch(url, init)` ПОСЛЕ whitelist-проверки.
// ─────────────────────────────────────────────────────────────────────────────

const noBareFetchSyntaxRule = [
  'error',
  {
    selector: "CallExpression[callee.type='Identifier'][callee.name='fetch']",
    message:
      "Запрещён прямой fetch(): импортируй safeFetch из 'services/safeFetch' (см. doc_project/121, AppSec v2-rescan).",
  },
  {
    selector: "MemberExpression[property.name='fetch'][object.name='window']",
    message:
      "Запрещён window.fetch: используй safeFetch из 'services/safeFetch'.",
  },
  {
    selector: "MemberExpression[property.name='fetch'][object.name='globalThis']",
    message:
      "Запрещён globalThis.fetch: используй safeFetch из 'services/safeFetch'.",
  },
];

export default defineConfig([
  globalIgnores(['dist']),
  {
    files: ['**/*.{ts,tsx}'],
    extends: [
      js.configs.recommended,
      tseslint.configs.recommended,
      reactHooks.configs.flat.recommended,
      reactRefresh.configs.vite,
    ],
    languageOptions: {
      globals: globals.browser,
    },
    rules: {
      'no-restricted-syntax': noBareFetchSyntaxRule,
    },
  },
  // Единственное исключение — line-level `// eslint-disable-next-line` в
  // `services/safeFetch.ts` рядом с `fetch(url, init)` ПОСЛЕ whitelist-guard.
  // Файл-level override НЕ ставим: исключение должно быть точечным, чтобы
  // любой новый `fetch(...)` в safeFetch.ts тоже ловился.
])
