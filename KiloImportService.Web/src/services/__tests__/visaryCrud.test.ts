/**
 * Unit-тесты для visaryCrud сервиса.
 */
import { describe, expect, it } from 'vitest';
import { getFinishingMaterialId, FINISHING_MATERIAL_MAP } from '../visaryCrud';

describe('getFinishingMaterialId', () => {
  it('возвращает правильный ID для "Черновая"', () => {
    expect(getFinishingMaterialId('Черновая')).toBe(3);
  });

  it('возвращает правильный ID для "Предчистовая"', () => {
    expect(getFinishingMaterialId('Предчистовая')).toBe(2);
  });

  it('возвращает правильный ID для "Чистовая"', () => {
    expect(getFinishingMaterialId('Чистовая')).toBe(1);
  });

  it('обрабатывает пробелы в начале и конце', () => {
    expect(getFinishingMaterialId('  Черновая  ')).toBe(3);
  });

  it('возвращает null для неизвестного типа', () => {
    expect(getFinishingMaterialId('Неизвестный')).toBe(null);
  });

  it('возвращает null для пустой строки', () => {
    expect(getFinishingMaterialId('')).toBe(null);
  });
});

describe('FINISHING_MATERIAL_MAP', () => {
  it('содержит все три типа отделки', () => {
    const keys = Object.keys(FINISHING_MATERIAL_MAP);
    expect(keys).toEqual(['Черновая', 'Предчистовая', 'Чистовая']);
  });

  it('ID соответствуют справочнику', () => {
    expect(FINISHING_MATERIAL_MAP['Черновая']).toBe(3);
    expect(FINISHING_MATERIAL_MAP['Предчистовая']).toBe(2);
    expect(FINISHING_MATERIAL_MAP['Чистовая']).toBe(1);
  });
});
