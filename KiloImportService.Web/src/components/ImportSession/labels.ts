/**
 * Презентационные подписи для статусов и стадий — единое место для всех компонентов
 * сессии (отчёт, прогресс, бейдж).
 */

import type { SessionStatus, StageKind } from '../../types/session';

export const SESSION_STATUS_LABELS: Record<SessionStatus, string> = {
  Pending: 'Ожидает',
  Parsing: 'Парсинг',
  Validating: 'Валидация',
  Validated: 'Готов к применению',
  Applying: 'Применение',
  Applied: 'Применено',
  Failed: 'Ошибка',
  Cancelled: 'Отменено',
};

export const STAGE_LABELS: Record<StageKind, string> = {
  Upload: 'Загрузка файла',
  Parse: 'Парсинг файла',
  Validate: 'Валидация строк',
  Apply: 'Запись в базу Visary',
};
