import { describe, expect, it } from 'vitest';
import { detectFileFormat, formatFileSize, SUPPORTED_FORMATS } from '../fileFormat.ts';

describe('detectFileFormat', () => {
  it('определяет CSV', () => {
    expect(detectFileFormat('data.csv')).toBe('csv');
    expect(detectFileFormat('data.CSV')).toBe('csv');
    expect(detectFileFormat('C:\\path\\to\\file.csv')).toBe('csv');
  });

  it('определяет XLS', () => {
    expect(detectFileFormat('data.xls')).toBe('xls');
  });

  it('определяет XLSB', () => {
    expect(detectFileFormat('data.xlsb')).toBe('xlsb');
  });

  it('определяет XLSX', () => {
    expect(detectFileFormat('data.xlsx')).toBe('xlsx');
    expect(detectFileFormat('test.Data.xlsx')).toBe('xlsx');
  });

  it('возвращает null для неподдерживаемого формата', () => {
    expect(detectFileFormat('data.txt')).toBe(null);
    expect(detectFileFormat('data.pdf')).toBe(null);
    expect(detectFileFormat('data.json')).toBe(null);
    expect(detectFileFormat('file')).toBe(null);
    expect(detectFileFormat('.gitignore')).toBe(null);
  });

  it('работает с именами файлов без расширения', () => {
    expect(detectFileFormat('README')).toBe(null);
    expect(detectFileFormat('.env')).toBe(null);
  });
});

describe('formatFileSize', () => {
  it('форматирует байты в КБ', () => {
    expect(formatFileSize(500)).toBe('500 Б');
    expect(formatFileSize(1024)).toBe('1.0 КБ');
    expect(formatFileSize(1536)).toBe('1.5 КБ');
    expect(formatFileSize(2048)).toBe('2.0 КБ');
  });

  it('форматирует байты в МБ', () => {
    expect(formatFileSize(1024 * 1024)).toBe('1.00 МБ');
    expect(formatFileSize(1.5 * 1024 * 1024)).toBe('1.50 МБ');
    expect(formatFileSize(5 * 1024 * 1024)).toBe('5.00 МБ');
  });
});

describe('SUPPORTED_FORMATS', () => {
  it('содержит все поддерживаемые форматы', () => {
    expect(SUPPORTED_FORMATS).toEqual(['csv', 'xls', 'xlsb', 'xlsx']);
  });
});
