import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import '@alfalab/core-components/vars/index.css'
import App from './App.tsx'
import { setAccessTokenGetter } from './services/auth'
import { apiUrl } from './services/apiUrl'
import { bootLog, bootWarn, criticalError } from './services/devLog'

// ─── Boot-логи (см. doc 148) ───
// Видно ВСЕГДА (prod-bundle тоже), чтобы при «белом экране» на целевом стенде
// можно было открыть DevTools-консоль и сразу понять, дошёл ли вообще main.tsx до
// браузера, с каким base-URL он собран, и какие API-префиксы будут использоваться.
bootLog('SPA boot start');
bootLog('build      :', __BUILD_TIME__);
bootLog('base       :', import.meta.env.BASE_URL);
bootLog('apiPrefix  :', apiUrl.prefix() || '(пусто, same-origin)');
bootLog('href       :', window.location.href);
bootLog('userAgent  :', navigator.userAgent);

// Глобальные обработчики необработанных ошибок и promise-rejection.
// До этого фронт молча падал в `init`-фазе на стенде — в DevTools-консоли видно
// было только стандартное «Uncaught (in promise)», без места и без контекста.
window.addEventListener('error', (e) => {
  criticalError('window.onerror', {
    message: e.message,
    source: e.filename,
    line: e.lineno,
    col: e.colno,
    error: e.error,
  });
});
window.addEventListener('unhandledrejection', (e) => {
  criticalError('unhandledrejection', e.reason);
});

// Поставщик access_token для запросов в backend (см. doc_project/111-incoming-jwt-auth.md).
// TODO: заменить заглушку на oidc-client-ts.getUser()?.access_token, когда будет
// подключён OIDC PKCE flow. Пока читаем токен из localStorage (dev-stub) —
// backend в dev-режиме (Auth:Authority пуст) запросы пропустит без токена.
setAccessTokenGetter(() => localStorage.getItem('access_token') ?? undefined)

const rootEl = document.getElementById('root');
if (!rootEl) {
  // Это никогда не должно случиться — index.html содержит <div id="root">. Но если
  // в bundle что-то перезаписало DOM до этого момента, без логи разобраться невозможно.
  criticalError('root element НЕ найден — index.html сломан или другой скрипт удалил <div id="root">');
} else {
  bootLog('mounting React root…');
  try {
    createRoot(rootEl).render(
      <StrictMode>
        <App />
      </StrictMode>,
    );
    bootLog('React mounted ✓');
  } catch (err) {
    criticalError('createRoot/render упал:', err);
    // Минимальный fallback-UI вместо белого экрана. По нему понятно,
    // что SPA дошла до браузера, но React-инициализация упала.
    rootEl.innerHTML =
      '<div style="font-family:sans-serif;padding:24px;color:#c00">' +
      '<h2>Не удалось запустить интерфейс</h2>' +
      '<p>Смотри DevTools-консоль (F12 → Console). Префикс сообщений — <code>[ab-fm-import]</code>.</p>' +
      '</div>';
  }
  // Глобальный bootWarn — пометить, что у нас есть нештатная конфигурация
  if (!apiUrl.prefix() && import.meta.env.PROD) {
    bootWarn('VITE_API_PREFIX пуст в prod-бандле — backend ожидается на same-origin (без префикса). ' +
      'Если backend опубликован под /api/ab-fm-import, пересобери фронт с VITE_API_PREFIX=/api/ab-fm-import (см. doc 147).');
  }
}
