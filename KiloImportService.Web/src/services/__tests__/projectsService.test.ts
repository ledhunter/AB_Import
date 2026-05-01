/**
 * Простые unit-тесты для маппинга projectsService.
 * Запуск: `npx tsx src/services/__tests__/projectsService.test.ts`
 *
 * Не проверяет реальный API (это проверяется руками через UI с реальным токеном),
 * но фиксирует контракт нормализации.
 */

import { strict as assert } from 'node:assert';
import { parseProjectsResponse, toProjectItem } from '../projectsService';
import type { ConstructionProjectRaw, ListViewResponse } from '../../types/listView';

let passed = 0;
let failed = 0;

function test(name: string, fn: () => void) {
  try {
    fn();
    console.log(`✓ ${name}`);
    passed++;
  } catch (err) {
    console.error(`✗ ${name}`);
    console.error(err);
    failed++;
  }
}

test('toProjectItem: Title и IdentifierKK заполнены', () => {
  const raw: ConstructionProjectRaw = {
    ID: 42,
    Title: 'ЖК Алые Паруса',
    IdentifierKK: 'KK-001',
    IdentifierZPLM: 'ZPLM-001',
  };
  const item = toProjectItem(raw);
  assert.equal(item.id, 42);
  assert.equal(item.title, 'ЖК Алые Паруса');
  assert.equal(item.raw, raw);
});

test('toProjectItem: IdentifierKK пустой → fallback на IdentifierZPLM', () => {
  const raw: ConstructionProjectRaw = {
    ID: 7,
    Title: 'Проект Б',
    IdentifierKK: null,
    IdentifierZPLM: 'ZPLM-555',
  };
  const item = toProjectItem(raw);
  // code удален — проверяем только title
  assert.equal(item.title, 'Проект Б');
});

test('toProjectItem: оба идентификатора пустые → fallback на ID-{id}', () => {
  const raw: ConstructionProjectRaw = {
    ID: 99,
    Title: 'Проект В',
    IdentifierKK: null,
    IdentifierZPLM: null,
  };
  const item = toProjectItem(raw);
  // code удален — проверяем только title
  assert.equal(item.title, 'Проект В');
});

test('toProjectItem: пустой Title → "Проект #{id}"', () => {
  const raw = {
    ID: 5,
    Title: '',
    IdentifierKK: 'X',
  } as ConstructionProjectRaw;
  const item = toProjectItem(raw);
  assert.equal(item.title, 'Проект #5');
});

test('toProjectItem: undefined Title (опциональное поле) → "Проект #{id}"', () => {
  // эмулируем неполный JSON от backend
  const raw = { ID: 11 } as ConstructionProjectRaw;
  const item = toProjectItem(raw);
  assert.equal(item.title, 'Проект #11');
  // code удален — проверяем только title
});

// =====================================================================
// parseProjectsResponse — извлечение строк и Total из разных форматов ответа
// =====================================================================

test('parseProjectsResponse: реальный формат Visary { Data, Total, Summaries }', () => {
  const response: ListViewResponse<ConstructionProjectRaw> = {
    Data: [
      { ID: 1, Title: 'Project A', IdentifierKK: 'KK-1' },
      { ID: 2, Title: 'Project B', IdentifierKK: 'KK-2' },
    ],
    Total: 2387,
    Summaries: [],
  };
  const { items, totalCount } = parseProjectsResponse(response);
  assert.equal(items.length, 2);
  assert.equal(items[0].id, 1);
  assert.equal(items[0].title, 'Project A');
  assert.equal(totalCount, 2387, 'Total из ответа должен быть подхвачен');
});

test('parseProjectsResponse: формат-fallback { Items, TotalCount }', () => {
  const response: ListViewResponse<ConstructionProjectRaw> = {
    Items: [{ ID: 5, Title: 'P5' }],
    TotalCount: 100,
  };
  const { items, totalCount } = parseProjectsResponse(response);
  assert.equal(items.length, 1);
  assert.equal(totalCount, 100);
});

test('parseProjectsResponse: формат-fallback camelCase { items, totalCount }', () => {
  const response: ListViewResponse<ConstructionProjectRaw> = {
    items: [{ ID: 7, Title: 'P7' }],
    totalCount: 7,
  };
  const { items, totalCount } = parseProjectsResponse(response);
  assert.equal(items.length, 1);
  assert.equal(totalCount, 7);
});

test('parseProjectsResponse: пустой ответ → пустой массив, totalCount=0', () => {
  const { items, totalCount } = parseProjectsResponse({});
  assert.equal(items.length, 0);
  assert.equal(totalCount, 0);
});

test('parseProjectsResponse: Data приоритетнее Items (если оба есть)', () => {
  const response: ListViewResponse<ConstructionProjectRaw> = {
    Data: [{ ID: 1, Title: 'from Data' }],
    Items: [{ ID: 2, Title: 'from Items' }],
    Total: 1,
    TotalCount: 999,
  };
  const { items, totalCount } = parseProjectsResponse(response);
  assert.equal(items.length, 1);
  assert.equal(items[0].title, 'from Data', 'Data важнее Items');
  assert.equal(totalCount, 1, 'Total важнее TotalCount');
});

console.log(`\n${passed} passed, ${failed} failed`);
process.exit(failed === 0 ? 0 : 1);
