/* eslint-disable */
// @ts-nocheck — тестовый файл под Node, исключён из tsconfig.app.json
/**
 * Тесты на сборку тела запроса Visary ListView.
 * Запуск: `npx tsx src/services/listView/__tests__/createListViewService.test.ts`
 *
 * Сетевая часть (`fetch`) НЕ тестируется здесь — она зависит от import.meta.env Vite
 * и проверяется руками через UI с реальным токеном (см. doc_project/08-visary-api-integration.md).
 */
import { strict as assert } from 'node:assert';
import { buildListViewRequestBody } from '../createListViewService';
import type { ListViewServiceConfig } from '../types';

interface FooRaw { ID: number }
interface FooItem { id: number }

const baseConfig: ListViewServiceConfig<FooRaw, FooItem> = {
  mnemonic: 'foo',
  columns: ['ID', 'Name'],
  toItem: (r) => ({ id: r.ID }),
};

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

test('buildListViewRequestBody: дефолты при пустом query', () => {
  const body = buildListViewRequestBody(baseConfig, {});
  assert.equal(body.Mnemonic, 'foo');
  assert.deepEqual(body.Columns, ['ID', 'Name']);
  assert.equal(body.PageSkip, 0);
  assert.equal(body.PageSize, 50, 'дефолтный PageSize = 50');
  assert.equal(body.Sorts, '[{"selector":"ID","desc":true}]', 'дефолтная сортировка по ID desc');
  assert.equal(body.Hidden, false);
  assert.equal(body.ExtraFilter, null);
  assert.equal(body.SearchString, '');
  assert.equal(body.AssociatedID, null);
});

test('buildListViewRequestBody: query переопределяет дефолты', () => {
  const body = buildListViewRequestBody(baseConfig, {
    pageSkip: 100,
    pageSize: 25,
    searchString: 'abc',
    extraFilter: '[["X","=",1]]',
    associatedId: 42,
    sorts: '[{"selector":"Title","desc":false}]',
  });
  assert.equal(body.PageSkip, 100);
  assert.equal(body.PageSize, 25);
  assert.equal(body.SearchString, 'abc');
  assert.equal(body.ExtraFilter, '[["X","=",1]]');
  assert.equal(body.AssociatedID, 42);
  assert.equal(body.Sorts, '[{"selector":"Title","desc":false}]');
});

test('buildListViewRequestBody: defaultPageSize из конфига применяется, если query.pageSize не задан', () => {
  const body = buildListViewRequestBody(
    { ...baseConfig, defaultPageSize: 200 },
    {},
  );
  assert.equal(body.PageSize, 200);
});

test('buildListViewRequestBody: query.pageSize важнее config.defaultPageSize', () => {
  const body = buildListViewRequestBody(
    { ...baseConfig, defaultPageSize: 200 },
    { pageSize: 10 },
  );
  assert.equal(body.PageSize, 10);
});

test('buildListViewRequestBody: defaultSort из конфига применяется, если query.sorts не задан', () => {
  const body = buildListViewRequestBody(
    { ...baseConfig, defaultSort: '[{"selector":"Created","desc":true}]' },
    {},
  );
  assert.equal(body.Sorts, '[{"selector":"Created","desc":true}]');
});

test('buildListViewRequestBody: signal не попадает в тело запроса', () => {
  const controller = new AbortController();
  const body = buildListViewRequestBody(baseConfig, { signal: controller.signal });
  assert.equal('signal' in body, false);
});

console.log(`\n${passed} passed, ${failed} failed`);
process.exit(failed === 0 ? 0 : 1);
