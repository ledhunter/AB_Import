/**
 * Поставщик access_token для исходящих запросов в backend.
 *
 * Backend (KiloImportService.Api) опционально валидирует JWT через тот же IdP,
 * что и Visary (см. doc_project/111-incoming-jwt-auth.md). Когда фронт логинится
 * в IdP — токен попадает сюда через `setAccessTokenGetter`, а `getAccessToken`
 * вызывается в `importsService.ts` (Authorization-header) и `importsHub.ts`
 * (`accessTokenFactory` SignalR, токен уйдёт в query `?access_token=…`).
 *
 * До подключения OIDC-flow на UI — getter возвращает `undefined`, backend в
 * dev-режиме (Auth:Authority пуст) запросы пропускает.
 */

export type TokenGetter = () => string | undefined | Promise<string | undefined>;

let _getter: TokenGetter = () => undefined;

/** Зарегистрировать поставщик токена. Вызвать один раз на старте приложения. */
export function setAccessTokenGetter(getter: TokenGetter): void {
  _getter = getter;
}

/** Получить актуальный access_token (или undefined, если auth не настроена). */
export async function getAccessToken(): Promise<string | undefined> {
  return _getter();
}
