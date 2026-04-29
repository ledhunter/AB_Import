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
    ConstructionPermissionNumber: 'РНС-001',
    ConstructionProjectNumber: 'CPN-77',
    StageNumber: 'Этап 2',
    ConstructionProjectID: 42,
  };
  const item = toSiteItem(raw);
  assert.equal(item.id, 1);
  assert.equal(item.title, 'Корпус 5');
  assert.equal(item.constructionPermissionNumber, 'РНС-001');
  assert.equal(item.constructionProjectNumber, 'CPN-77');
  assert.equal(item.stageNumber, 'Этап 2');
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
    ConstructionPermissionNumber: null,
    ConstructionProjectNumber: undefined,
    StageNumber: '',
  });
  assert.equal(item.constructionPermissionNumber, '');
  assert.equal(item.constructionProjectNumber, '');
  assert.equal(item.stageNumber, '');
});

test('buildSitesQueryByProject: формирует ExtraFilter и AssociatedID', () => {
  const q = buildSitesQueryByProject(123);
  assert.equal(q.associatedId, 123);
  assert.equal(q.extraFilter, '[["ConstructionProjectID","=",123]]');
});

test('buildSitesQueryByProject: сохраняет переданные пагинационные параметры', () => {
  const q = buildSitesQueryByProject(7, { pageSkip: 50, pageSize: 25, searchString: 'foo' });
  assert.equal(q.pageSkip, 50);
  assert.equal(q.pageSize, 25);
  assert.equal(q.searchString, 'foo');
  assert.equal(q.associatedId, 7);
});

console.log(`\n${passed} passed, ${failed} failed`);
process.exit(failed === 0 ? 0 : 1);
