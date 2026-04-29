import { defineConfig, loadEnv } from 'vite';
import type { ProxyOptions } from 'vite';
import react from '@vitejs/plugin-react';

// https://vite.dev/config/
export default defineConfig(({ mode }) => {
  const env = loadEnv(mode, process.cwd(), '');
  const visaryTarget = env.VITE_VISARY_API_URL || 'https://isup-alfa-test.k8s.npc.ba';
  const backendTarget = env.VITE_BACKEND_URL || 'http://localhost:5000';

  // Логирование одного proxy-канала: req/res/error в формате `[Vite proxy → tag]`.
  // Используется и для Visary, и для собственного backend — чтобы было видно,
  // куда конкретно ушёл запрос.
  const logging =
    (tag: string, target: string): ProxyOptions['configure'] =>
    (proxy) => {
      proxy.on('proxyReq', (_proxyReq, req) => {
        console.log(`[Vite proxy → ${tag}] → ${req.method} ${target}${req.url}`);
      });
      proxy.on('proxyRes', (proxyRes, req) => {
        console.log(
          `[Vite proxy → ${tag}] ← ${proxyRes.statusCode} ${req.method} ${req.url}`,
        );
      });
      proxy.on('error', (err, req) => {
        console.error(
          `[Vite proxy → ${tag}] ✗ ERROR ${req.method} ${req.url} —`,
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
        '/hubs': backendProxy({ ws: true }),
        '/health': backendProxy(),
        '/swagger': backendProxy(),
      },
    },
  };
});
