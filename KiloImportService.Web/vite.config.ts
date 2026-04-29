import { defineConfig, loadEnv } from 'vite';
import react from '@vitejs/plugin-react';

// https://vite.dev/config/
export default defineConfig(({ mode }) => {
  const env = loadEnv(mode, process.cwd(), '');
  const visaryTarget = env.VITE_VISARY_API_URL || 'https://isup-alfa-test.k8s.npc.ba';

  return {
    plugins: [react()],
    server: {
      proxy: {
        // Прокси на Visary API чтобы обойти CORS из dev-сервера.
        // /api/visary/* → {visaryTarget}/api/visary/*
        '/api/visary': {
          target: visaryTarget,
          changeOrigin: true,
          secure: true,
          configure: (proxy) => {
            proxy.on('proxyReq', (_proxyReq, req) => {
              // eslint-disable-next-line no-console
              console.log(`[Vite proxy] → ${req.method} ${visaryTarget}${req.url}`);
            });
            proxy.on('proxyRes', (proxyRes, req) => {
              // eslint-disable-next-line no-console
              console.log(
                `[Vite proxy] ← ${proxyRes.statusCode} ${req.method} ${req.url}`,
              );
            });
            proxy.on('error', (err, req) => {
              // eslint-disable-next-line no-console
              console.error(
                `[Vite proxy] ✗ ERROR ${req.method} ${req.url} —`,
                err.message,
              );
            });
          },
        },
      },
    },
  };
});
