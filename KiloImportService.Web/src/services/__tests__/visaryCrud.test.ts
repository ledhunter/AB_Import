import { describe, expect, it, vi } from 'vitest';
import { getFinishingMaterialId, FINISHING_MATERIAL_MAP } from '../visaryCrud.ts';

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
    expect(getFinishingMaterialId('\tПредчистовая\n')).toBe(2);
  });

  it('возвращает null для неизвестного типа', () => {
    expect(getFinishingMaterialId('Неизвестный')).toBe(null);
    expect(getFinishingMaterialId('Unknown')).toBe(null);
  });

  it('возвращает null для пустой строки', () => {
    expect(getFinishingMaterialId('')).toBe(null);
    expect(getFinishingMaterialId('   ')).toBe(null);
  });

  it('чувствителен к регистру', () => {
    expect(getFinishingMaterialId('черновая')).toBe(null);
    expect(getFinishingMaterialId('ЧЕРНОВАЯ')).toBe(null);
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

  it('ID непрерывные (для проверки валидности справочника)', () => {
    const ids = Object.values(FINISHING_MATERIAL_MAP);
    expect(ids).toEqual([3, 2, 1]);
  });
});
