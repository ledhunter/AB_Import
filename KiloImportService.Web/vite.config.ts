import path from 'node:path';
import { createRequire } from 'node:module';
import { defineConfig, loadEnv } from 'vite';
import type { Plugin, ProxyOptions } from 'vite';
import react from '@vitejs/plugin-react';

// Single Source Of Truth для env-переменных — корневой `.env` репозитория.
// Привязываемся к `process.cwd()`, т.к. `import.meta.url` в `vite.config.ts`
// может указывать на временный bundle при предкомпиляции конфига и давать
// неверную базу для `path.resolve` (см. doc 133 v1.2). vite всегда запускается
// из каталога с `package.json` — `cwd` стабилен.
const projectRoot = process.cwd();
const envDir = path.resolve(projectRoot, '..');

// Резолвер модулей относительно корня проекта (KiloImportService.Web).
// `createRequire(<absolute path>)` даёт Node-резолвер, который ищет
// `node_modules` начиная с указанной директории — точно как `require()`
// в обычной Node-программе. Используется в плагине ниже, чтобы получить
// гарантированно АБСОЛЮТНЫЙ путь до файла, существование которого Node
// уже проверил.
const requireFromProject = createRequire(path.join(projectRoot, 'package.json'));

// Vite 8 использует Rolldown как сборщик. Rolldown строже резолвит подпути
// пакетов: без явного `exports` в package.json он НЕ раскрывает
// `@alfalab/core-components-config/esm` (директория) в `esm/index.js`,
// хотя Node.js / Webpack / старый Rollup это делают.
//
// Пакеты `@alfalab/core-components-*` массово (>120 шт.) импортируют сиблингов
// через подпуть `/esm` (директория), а их `package.json` содержит только
// `"main"`/`"module"` без поля `"exports"`. Подтверждённые нарушители:
// `core-components-config`, `core-components-shared`,
// `core-components-stack-context`, … (см. doc 133).
//
// Custom resolver-плагин (не `resolve.alias`-замена-строкой!) вызывает
// стандартный Node.js-резолвер `require.resolve`. Он:
//   1. Возвращает гарантированно АБСОЛЮТНЫЙ путь (Rolldown это требует).
//   2. Сам проверяет существование файла — если пакета нет, кидает MODULE_NOT_FOUND,
//      плагин это ловит и возвращает `null` (резолв продолжается дальше).
//   3. Не зависит от seman'ики `string.replace($1)` — это была причина
//      «UNLOADABLE_DEPENDENCY: os error 2» в Docker (см. doc 133 v1.2).
//
// `enforce: 'pre'` — плагин срабатывает ДО встроенного резолвера Rolldown,
// чтобы успеть подменить подпуть до того, как Rolldown попробует его и провалится.
const alfalabEsmDirAlias: Plugin = {
  name: 'alfalab-esm-dir-alias',
  enforce: 'pre',
  resolveId(source) {
    const match = /^(@alfalab\/core-components-[^/]+)\/esm$/.exec(source);
    if (!match) return null;
    try {
      // Возвращает что-то вроде `/app/node_modules/@alfalab/core-components-config/esm/index.js`
      // (Linux/Docker) или `C:\…\node_modules\@alfalab\core-components-config\esm\index.js` (Windows).
      // Оба формата валидны для Rolldown.
      return requireFromProject.resolve(`${match[1]}/esm/index.js`);
    } catch {
      return null;
    }
  },
};

// https://vite.dev/config/
export default defineConfig(({ command, mode }) => {
  const env = loadEnv(mode, envDir, '');
  // VITE_VISARY_API_URL — маппинг от SSOT-переменной VISARY_BASE_URL в корневом .env
  // (см. docker-compose.yml + doc_project/122-environment-config.md).
  // Deny-by-default ТОЛЬКО для `vite serve` (dev): хост test-стенда не должен
  // уехать случайно в preprod/prod (см. doc 121/122).
  // При `vite build` (prod-bundle для k8s / Jenkins CI) переменная НЕ нужна:
  // server.proxy используется только dev-server'ом; в prod URL подставляется
  // в bundle на runtime через nginx env-substitution / k8s ConfigMap (doc 130).
  // Иначе сборка CI блокируется требованием dev-only переменной.
  const visaryTarget = env.VITE_VISARY_API_URL || '';
  if (command === 'serve' && !visaryTarget) {
    throw new Error(
      'VITE_VISARY_API_URL пуст. Скопируй .env.example → .env и задай VISARY_BASE_URL ' +
        '(маппинг → VITE_VISARY_API_URL см. в docker-compose.yml). См. doc_project/122.',
    );
  }
  const backendTarget = env.VITE_BACKEND_URL || 'http://localhost:5000';

  // Логирование одного proxy-канала: req/res/error в формате `[Vite proxy → tag]`.
  // Используется и для Visary, и для собственного backend — чтобы было видно,
  // куда конкретно ушёл запрос.
  const logging =
    (tag: string, target: string): ProxyOptions['configure'] =>
    (proxy) => {
      // Константные format-строки (закрывает unsafe-formatstring, см. doc_project/121).
      proxy.on('proxyReq', (_proxyReq, req) => {
        console.log('[Vite proxy → %s] → %s %s%s', tag, req.method, target, req.url);
      });
      proxy.on('proxyRes', (proxyRes, req) => {
        console.log(
          '[Vite proxy → %s] ← %s %s %s',
          tag,
          proxyRes.statusCode,
          req.method,
          req.url,
        );
      });
      proxy.on('error', (err, req) => {
        console.error(
          '[Vite proxy → %s] ✗ ERROR %s %s — %s',
          tag,
          req.method,
          req.url,
          err.message,
        );
      });
    };

  const backendProxy = (extra: Partial<ProxyOptions> = {}): ProxyOptions => ({
    target: backendTarget,
    changeOrigin: true,
    secure: false,
    configure: logging('backend', backendTarget),
    ...extra,
  });

  return {
    // Custom resolver-плагин ставим ПЕРЕД react() — он легковесный и срабатывает
    // только на узкий regex (@alfalab/core-components-*/esm), не мешает остальному.
    plugins: [alfalabEsmDirAlias, react()],
    envDir,
    build: {
      // Vite 8 перешёл на lightningcss как дефолтный CSS-минификатор.
      // lightningcss — строгий парсер, падает на нестандартном синтаксисе
      // в CSS опубликованных пакетов (`@alfalab/core-components` содержит
      // template-плейсхолдеры `$(varName)` в CSS, остатки внутр. сборки).
      // esbuild (старый дефолт) толерантен к таким конструкциям и так же
      // быстр. Откатываем минификатор обратно, чтобы пройти prod-build
      // без правок чужих пакетов.
      cssMinify: 'esbuild',
    },
    server: {
      proxy: {
        // ─── Visary (внешний API через прокси, чтобы обойти CORS) ───
        // /api/visary/* → {visaryTarget}/api/visary/*
        '/api/visary': {
          target: visaryTarget,
          changeOrigin: true,
          secure: true,
          configure: logging('visary', visaryTarget),
        },

        // ─── Собственный backend (KiloImportService.Api) ───
        // ⚠️ Объявлены ДО общего /api, чтобы перекрыть generic-маршруты.
        // SignalR использует WebSocket → ws: true для /hubs.
        '/api/imports': backendProxy(),
        '/api/import-types': backendProxy(),
        '/api/projects': backendProxy(),
        '/api/sites': backendProxy(),
        '/hubs': backendProxy({ ws: true }),
        '/health': backendProxy(),
        '/swagger': backendProxy(),
      },
    },
  };
});
