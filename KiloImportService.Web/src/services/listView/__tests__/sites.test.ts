/* eslint-disable */
// @ts-nocheck — тестовый файл под Node, исключён из tsconfig.app.json
/**
 * Тесты маппера и query-хелпера для объектов строительства.
 * Запуск: `npx tsx src/services/listView/__tests__/sites.test.ts`
 */
import { strict as assert } from 'node:assert';
import { buildSitesQueryByProject, toSiteItem } from '../entities/sites';
import type { ConstructionSiteRaw } from '../../../types/listView';

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

test('toSiteItem: все поля заполнены → точное соответствие', () => {
  const raw: ConstructionSiteRaw = {
    ID: 1,
    Title: 'Корпус 5',
    Address: 'ул. Ленина, 10',
    ConstructionProjectNumber: 'CPN-77',
    Type: 'Жилой',
    TotalArea: 1500,
  };
  const item = toSiteItem(raw);
  assert.equal(item.id, 1);
  assert.equal(item.title, 'Корпус 5');
  assert.equal(item.address, 'ул. Ленина, 10');
  assert.equal(item.constructionProjectNumber, 'CPN-77');
  assert.equal(item.type, 'Жилой');
  assert.equal(item.totalArea, 1500);
  assert.equal(item.raw, raw);
});

test('toSiteItem: пустой Title → "Объект #{id}"', () => {
  const item = toSiteItem({ ID: 5, Title: '' });
  assert.equal(item.title, 'Объект #5');
});

test('toSiteItem: undefined Title → "Объект #{id}"', () => {
  const item = toSiteItem({ ID: 9 });
  assert.equal(item.title, 'Объект #9');
});

test('toSiteItem: пустые опциональные строки → пустые строки в UI-объекте (не undefined)', () => {
  const item = toSiteItem({
    ID: 3,
    Title: 'X',
    Address: null,
    ConstructionProjectNumber: undefined,
    Type: '',
  });
  assert.equal(item.address, '');
  assert.equal(item.constructionProjectNumber, '');
  assert.equal(item.type, '');
});

test('buildSitesQueryByProject: формирует AssociationFilter', () => {
  const q = buildSitesQueryByProject(123);
  assert.deepEqual(q.associationFilter, {
    AssociatedId: 123,
    Filters: null,
  });
});

test('buildSitesQueryByProject: сохраняет переданные пагинационные параметры', () => {
  const q = buildSitesQueryByProject(7, { pageSkip: 50, pageSize: 25, searchString: 'foo' });
  assert.equal(q.pageSkip, 50);
  assert.equal(q.pageSize, 25);
  assert.equal(q.searchString, 'foo');
  assert.deepEqual(q.associationFilter, {
    AssociatedId: 7,
    Filters: null,
  });
});

console.log(`\n${passed} passed, ${failed} failed`);
process.exit(failed === 0 ? 0 : 1);
