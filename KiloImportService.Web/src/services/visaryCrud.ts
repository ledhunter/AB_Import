/**
 * Сервис для работы с Visary CRUD API.
 * 
 * Endpoints:
 * - GET  /api/visary/crud/{entity}/{id} - получить сущность
 * - PATCH /api/visary/crud/{entity}/{id}?forceUpdate=true - обновить сущность
 */

import { visaryGet, visaryPatch } from './visaryApi';

export interface ConstructionSiteEntity {
  ID: number;
  RowVersion: number;
  Title?: string;
  FinishingMaterial?: {
    ID: number;
    Title?: string;
  } | null;
  [key: string]: unknown;
}

export interface UpdateConstructionSitePayload {
  ID: number;
  RowVersion: number;
  FinishingMaterial?: {
    ID: number;
  } | null;
}

/**
 * Получить объект строительства по ID.
 * Возвращает полную сущность с RowVersion.
 */
export async function getConstructionSite(
  siteId: number,
  signal?: AbortSignal,
): Promise<ConstructionSiteEntity> {
  const path = `/crud/constructionsite/${siteId}`;
  console.info(`[VisaryCRUD] → GET ${path}`);
  
  const entity = await visaryGet<ConstructionSiteEntity>(path, { signal });
  
  console.info(`[VisaryCRUD] ← GET ${path} | RowVersion=${entity.RowVersion}`);
  return entity;
}

/**
 * Обновить объект строительства.
 * Требует актуальный RowVersion для оптимистичной блокировки.
 */
export async function updateConstructionSite(
  siteId: number,
  payload: UpdateConstructionSitePayload,
  signal?: AbortSignal,
): Promise<ConstructionSiteEntity> {
  const path = `/crud/constructionsite/${siteId}`;
  console.info(`[VisaryCRUD] → PATCH ${path}?forceUpdate=true`, payload);
  
  const entity = await visaryPatch<ConstructionSiteEntity>(path, payload, {
    signal,
    queryParams: { forceUpdate: 'true' },
  });
  
  console.info(`[VisaryCRUD] ← PATCH ${path} | успешно обновлено`);
  return entity;
}

/**
 * Справочник "Тип отделки" (FinishingMaterial).
 * Маппинг название -> ID.
 */
export const FINISHING_MATERIAL_MAP: Record<string, number> = {
  'Черновая': 3,
  'Предчистовая': 2,
  'Чистовая': 1,
};

/**
 * Получить ID типа отделки по названию.
 * Возвращает null если название не найдено.
 */
export function getFinishingMaterialId(title: string): number | null {
  const normalized = title.trim();
  return FINISHING_MATERIAL_MAP[normalized] ?? null;
}

/**
 * Обновить тип отделки (FinishingMaterial) объекта строительства.
 * 
 * Алгоритм:
 * 1. Получить текущую сущность с RowVersion
 * 2. Обновить поле FinishingMaterial
 * 
 * @param siteId - ID объекта строительства
 * @param finishingMaterialTitle - название типа отделки ("Черновая", "Предчистовая", "Чистовая")
 * @param signal - AbortSignal для отмены запроса
 * @returns обновлённая сущность
 * @throws Error если тип отделки не найден в справочнике
 */
export async function updateFinishingMaterial(
  siteId: number,
  finishingMaterialTitle: string,
  signal?: AbortSignal,
): Promise<ConstructionSiteEntity> {
  console.info(
    `[updateFinishingMaterial] Обновление объекта ${siteId}: FinishingMaterial="${finishingMaterialTitle}"`,
  );

  // 1. Получить ID типа отделки из справочника
  const finishingMaterialId = getFinishingMaterialId(finishingMaterialTitle);
  if (finishingMaterialId === null) {
    throw new Error(
      `Тип отделки "${finishingMaterialTitle}" не найден в справочнике. Доступные: ${Object.keys(FINISHING_MATERIAL_MAP).join(', ')}`,
    );
  }

  // 2. Получить текущую сущность с RowVersion
  const currentEntity = await getConstructionSite(siteId, signal);
  console.info(
    `[updateFinishingMaterial] Получен объект ${siteId}, RowVersion=${currentEntity.RowVersion}`,
  );

  // 3. Обновить FinishingMaterial
  const payload: UpdateConstructionSitePayload = {
    ID: siteId,
    RowVersion: currentEntity.RowVersion,
    FinishingMaterial: {
      ID: finishingMaterialId,
    },
  };

  const updatedEntity = await updateConstructionSite(siteId, payload, signal);
  console.info(
    `[updateFinishingMaterial] ✓ Объект ${siteId} обновлён: FinishingMaterial.ID=${finishingMaterialId}`,
  );

  return updatedEntity;
}
