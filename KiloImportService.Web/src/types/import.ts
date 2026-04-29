/**
 * Доменные типы UI, не привязанные к backend-DTO.
 *
 * Сессионные модели (UiSession/UiReport/RowStatus/SessionStatus) — в `types/session.ts`.
 * Backend-DTO (ApiImportSession etc.) — в `types/api.ts`.
 */

/**
 * Открытый union: реальный список приходит из `GET /api/import-types`.
 * Хранится как строка, чтобы не пересобирать тип при добавлении нового импорта.
 */
export type ImportType = string;

/** Расширения файлов, поддерживаемые UI-валидацией. */
export type FileFormat = 'csv' | 'xls' | 'xlsb' | 'xlsx';
