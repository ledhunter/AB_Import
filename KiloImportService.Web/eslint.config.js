import js from '@eslint/js'
import globals from 'globals'
import reactHooks from 'eslint-plugin-react-hooks'
import reactRefresh from 'eslint-plugin-react-refresh'
import tseslint from 'typescript-eslint'
import { defineConfig, globalIgnores } from 'eslint/config'

// ─────────────────────────────────────────────────────────────────────────────
// Кастомные правила безопасности. См. doc_project/121-security-fixes-appsec-v1.md
// (§ AppSec v2…v6, § Анти-паттерны).
//
//  • no-restricted-syntax запрещает ВСЕ способы инициировать HTTP-запрос в обход
//    `services/safeFetch.ts`:
//      ❌ `fetch(...)`           — прямой вызов глобала
//      ❌ `window.fetch(...)`    — через window
//      ❌ `globalThis.fetch(...)`— через globalThis
//      ❌ `new XMLHttpRequest()` — низкоуровневый XHR-API (v6 fix: safeFetch
//         сам поверх XHR — единственная разрешённая точка инстанцирования)
//
// Все сетевые запросы обязаны идти через `safeFetch(url, init)`. Внутри:
// 5 guard'ов (тип / non-empty / protocol-relative / same-origin абс. путь /
// traversal) + whitelist API-корней. Это закрывает SSRF структурно.
//
// Один файл — `services/safeFetch.ts` — содержит line-level
// `// eslint-disable-next-line no-restricted-syntax` ровно на той строке, где
// единственный `new XMLHttpRequest()` создаётся ПОСЛЕ guard'ов. Файл-level
// override НЕ ставим: любой новый sink-вызов в safeFetch.ts тоже должен ловиться.
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
  {
    selector: "NewExpression[callee.name='XMLHttpRequest']",
    message:
      "Запрещён прямой new XMLHttpRequest(): используй safeFetch из 'services/safeFetch' (см. doc_project/121, AppSec v6 — XHR-transition).",
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
