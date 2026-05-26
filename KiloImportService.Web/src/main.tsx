import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import '@alfalab/core-components/vars/index.css'
import App from './App.tsx'
import { setAccessTokenGetter } from './services/auth'

// Поставщик access_token для запросов в backend (см. doc_project/111-incoming-jwt-auth.md).
// TODO: заменить заглушку на oidc-client-ts.getUser()?.access_token, когда будет
// подключён OIDC PKCE flow. Пока читаем токен из localStorage (dev-stub) —
// backend в dev-режиме (Auth:Authority пуст) запросы пропустит без токена.
setAccessTokenGetter(() => localStorage.getItem('access_token') ?? undefined)

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <App />
  </StrictMode>,
)
