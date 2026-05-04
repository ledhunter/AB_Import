/**
 * Сервис синхронизации объектов строительства с локальной БД.
 *
 * Вызывается при выборе объекта в UI, чтобы добавить его в локальную БД
 * перед запуском импорта.
 */

import { VisaryApiError } from './visaryApi';

export interface SiteSyncResult {
  success: boolean;
  id: number;
}

export async function syncSite(siteId: number, projectId: number): Promise<SiteSyncResult> {
  const requestId = Math.random().toString(36).slice(2, 8);

  console.info(`[SitesSync] → POST /api/sites/sync/${siteId}?projectId=${projectId} #${requestId}`);

  const response = await fetch(`/api/sites/sync/${siteId}?projectId=${projectId}`, {
    method: 'POST',
  });

  const ms = Math.round(performance.now() - performance.now());

  if (!response.ok) {
    const errBody = await response.text().catch(() => undefined);
    console.error(
      `[SitesSync] ✗ ${response.status} ${response.statusText} /api/sites/sync/${siteId} #${requestId} (${ms}ms)`,
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
  console.info(`[SitesSync] ← ${response.status} /api/sites/sync/${siteId} #${requestId} (${ms}ms)`, data);
  
  return data;
}
