/**
 * Unit-тесты для visaryCrud сервиса.
 *
 * Запуск: npx tsx src/services/__tests__/visaryCrud.test.ts
 */

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { getFinishingMaterialId, FINISHING_MATERIAL_MAP } from '../visaryCrud';

test('getFinishingMaterialId: возвращает правильный ID для "Черновая"', () => {
  assert.equal(getFinishingMaterialId('Черновая'), 3);
});

test('getFinishingMaterialId: возвращает правильный ID для "Предчистовая"', () => {
  assert.equal(getFinishingMaterialId('Предчистовая'), 2);
});

test('getFinishingMaterialId: возвращает правильный ID для "Чистовая"', () => {
  assert.equal(getFinishingMaterialId('Чистовая'), 1);
});

test('getFinishingMaterialId: обрабатывает пробелы в начале и конце', () => {
  assert.equal(getFinishingMaterialId('  Черновая  '), 3);
});

test('getFinishingMaterialId: возвращает null для неизвестного типа', () => {
  assert.equal(getFinishingMaterialId('Неизвестный'), null);
});

test('getFinishingMaterialId: возвращает null для пустой строки', () => {
  assert.equal(getFinishingMaterialId(''), null);
});

test('FINISHING_MATERIAL_MAP: содержит все три типа отделки', () => {
  const keys = Object.keys(FINISHING_MATERIAL_MAP);
  assert.deepEqual(keys, ['Черновая', 'Предчистовая', 'Чистовая']);
});

test('FINISHING_MATERIAL_MAP: ID соответствуют справочнику', () => {
  assert.equal(FINISHING_MATERIAL_MAP['Черновая'], 3);
  assert.equal(FINISHING_MATERIAL_MAP['Предчистовая'], 2);
  assert.equal(FINISHING_MATERIAL_MAP['Чистовая'], 1);
});

console.log('✓ Все тесты visaryCrud пройдены успешно');
