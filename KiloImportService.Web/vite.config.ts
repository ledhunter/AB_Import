import path from 'node:path';
import { defineConfig, loadEnv } from 'vite';
import type { ProxyOptions } from 'vite';
import react from '@vitejs/plugin-react';

// Single Source Of Truth для env-переменных — корневой `.env` репозитория.
// Был дубль `KiloImportService.Web/.env.local` — удалён, потому что `VITE_VISARY_API_TOKEN`
// и так нужен docker-compose'у в корневом `.env`. Теперь токен живёт ровно в одном файле.
const envDir = path.resolve(process.cwd(), '..');

// https://vite.dev/config/
export default defineConfig(({ mode }) => {
  const env = loadEnv(mode, envDir, '');
  const visaryTarget = env.VITE_VISARY_API_URL || 'https://isup-alfa-test.k8s.npc.ba';
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
    plugins: [react()],
    envDir,
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
