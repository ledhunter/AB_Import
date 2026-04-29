/* eslint-disable */
// @ts-nocheck — тестовый файл под Node, исключён из tsconfig.app.json
/**
 * Тесты generic-парсера ответов Visary ListView.
 * Запуск: `npx tsx src/services/listView/__tests__/parseListViewResponse.test.ts`
 */
import { strict as assert } from 'node:assert';
import { parseListViewResponse } from '../parseListViewResponse';
import type { ListViewResponseRaw } from '../types';

interface FooRaw { ID: number; Name?: string }
interface FooItem { id: number; name: string }

const toFoo = (r: FooRaw): FooItem => ({ id: r.ID, name: r.Name || `Foo #${r.ID}` });

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

test('parseListViewResponse: реальный формат Visary { Data, Total, Summaries }', () => {
  const raw: ListViewResponseRaw<FooRaw> = {
    Data: [{ ID: 1, Name: 'A' }, { ID: 2, Name: 'B' }],
    Total: 2387,
    Summaries: [],
  };
  const { items, totalCount } = parseListViewResponse(raw, toFoo);
  assert.equal(items.length, 2);
  assert.equal(items[0].id, 1);
  assert.equal(items[0].name, 'A');
  assert.equal(totalCount, 2387);
});

test('parseListViewResponse: fallback { Items, TotalCount }', () => {
  const raw: ListViewResponseRaw<FooRaw> = {
    Items: [{ ID: 5, Name: 'X' }],
    TotalCount: 100,
  };
  const { items, totalCount } = parseListViewResponse(raw, toFoo);
  assert.equal(items.length, 1);
  assert.equal(totalCount, 100);
});

test('parseListViewResponse: fallback camelCase { items, totalCount }', () => {
  const raw: ListViewResponseRaw<FooRaw> = {
    items: [{ ID: 7, Name: 'Y' }],
    totalCount: 7,
  };
  const { items, totalCount } = parseListViewResponse(raw, toFoo);
  assert.equal(items.length, 1);
  assert.equal(totalCount, 7);
});

test('parseListViewResponse: пустой ответ → []  и totalCount=0', () => {
  const { items, totalCount } = parseListViewResponse<FooRaw, FooItem>({}, toFoo);
  assert.equal(items.length, 0);
  assert.equal(totalCount, 0);
});

test('parseListViewResponse: Data приоритетнее Items, Total приоритетнее TotalCount', () => {
  const raw: ListViewResponseRaw<FooRaw> = {
    Data: [{ ID: 1, Name: 'from Data' }],
    Items: [{ ID: 2, Name: 'from Items' }],
    Total: 1,
    TotalCount: 999,
  };
  const { items, totalCount } = parseListViewResponse(raw, toFoo);
  assert.equal(items[0].name, 'from Data');
  assert.equal(totalCount, 1);
});

test('parseListViewResponse: totalCount по умолчанию = items.length, если не передан', () => {
  const raw: ListViewResponseRaw<FooRaw> = {
    Data: [{ ID: 1 }, { ID: 2 }, { ID: 3 }],
  };
  const { items, totalCount } = parseListViewResponse(raw, toFoo);
  assert.equal(items.length, 3);
  assert.equal(totalCount, 3);
});

console.log(`\n${passed} passed, ${failed} failed`);
process.exit(failed === 0 ? 0 : 1);
