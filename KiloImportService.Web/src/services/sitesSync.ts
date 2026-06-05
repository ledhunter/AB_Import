/**
 * Сервис синхронизации объектов строительства с локальной БД.
 *
 * Вызывается при выборе объекта в UI, чтобы добавить его в локальную БД
 * перед запуском импорта.
 */

import { apiUrl } from './apiUrl';
import { devError, devInfo } from './devLog';
import { VisaryApiError } from './visaryApi';

export interface SiteSyncResult {
  success: boolean;
  id: number;
}

export async function syncSite(siteId: number, projectId: number): Promise<SiteSyncResult> {
  const requestId = Math.random().toString(36).slice(2, 8);
  const url = apiUrl.sites(`/sync/${siteId}?projectId=${projectId}`);

  devInfo(`[SitesSync] → POST ${url} #${requestId}`);

  const start = performance.now();
  const response = await fetch(url, {
    method: 'POST',
  });

  const ms = Math.round(performance.now() - start);

  if (!response.ok) {
    const errBody = await response.text().catch(() => undefined);
    devError(
      `[SitesSync] ✗ ${response.status} ${response.statusText} ${url} #${requestId} (${ms}ms)`,
      errBody,
    );

    if (response.status === 404) {
      throw new VisaryApiError(`Объект строительства ${siteId} не найден в Visary`, 404, errBody);
    }

    throw new VisaryApiError(
      `Синхронизация объекта ${siteId} не удалась: ${response.status} ${response.statusText}`,
      response.status,
      errBody,
    );
  }

  const data = (await response.json()) as SiteSyncResult;
  devInfo(`[SitesSync] ← ${response.status} ${url} #${requestId} (${ms}ms)`, data);

  return data;
}
