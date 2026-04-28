import type { FileFormat } from '../types/import';

export const SUPPORTED_FORMATS: FileFormat[] = ['csv', 'xls', 'xlsb', 'xlsx'];

export const ACCEPT_ALL_SUPPORTED =
  '.csv,.xls,.xlsb,.xlsx,' +
  'text/csv,' +
  'application/vnd.ms-excel,' +
  'application/vnd.ms-excel.sheet.binary.macroEnabled.12,' +
  'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet';

/**
 * Определяет формат файла по расширению.
 * Возвращает null, если формат не поддерживается.
 */
export const detectFileFormat = (fileName: string): FileFormat | null => {
  const lower = fileName.toLowerCase();
  const dot = lower.lastIndexOf('.');
  if (dot === -1) return null;
  const ext = lower.slice(dot + 1) as FileFormat;
  return SUPPORTED_FORMATS.includes(ext) ? ext : null;
};

export const formatFileSize = (bytes: number): string => {
  if (bytes < 1024) return `${bytes} Б`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} КБ`;
  return `${(bytes / 1024 / 1024).toFixed(2)} МБ`;
};
