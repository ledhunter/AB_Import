/**
 * Тесты generic-парсера ответов Visary ListView.
 */
import { describe, expect, it } from 'vitest';
import { parseListViewResponse } from '../parseListViewResponse';
import type { ListViewResponseRaw } from '../types';

interface FooRaw { ID: number; Name?: string }
interface FooItem { id: number; name: string }

const toFoo = (r: FooRaw): FooItem => ({ id: r.ID, name: r.Name || `Foo #${r.ID}` });

describe('parseListViewResponse', () => {
  it('реальный формат Visary { Data, Total, Summaries }', () => {
    const raw: ListViewResponseRaw<FooRaw> = {
      Data: [{ ID: 1, Name: 'A' }, { ID: 2, Name: 'B' }],
      Total: 2387,
      Summaries: [],
    };
    const { items, totalCount } = parseListViewResponse(raw, toFoo);
    expect(items.length).toBe(2);
    expect(items[0].id).toBe(1);
    expect(items[0].name).toBe('A');
    expect(totalCount).toBe(2387);
  });

  it('fallback { Items, TotalCount }', () => {
    const raw: ListViewResponseRaw<FooRaw> = {
      Items: [{ ID: 5, Name: 'X' }],
      TotalCount: 100,
    };
    const { items, totalCount } = parseListViewResponse(raw, toFoo);
    expect(items.length).toBe(1);
    expect(totalCount).toBe(100);
  });

  it('fallback camelCase { items, totalCount }', () => {
    const raw: ListViewResponseRaw<FooRaw> = {
      items: [{ ID: 7, Name: 'Y' }],
      totalCount: 7,
    };
    const { items, totalCount } = parseListViewResponse(raw, toFoo);
    expect(items.length).toBe(1);
    expect(totalCount).toBe(7);
  });

  it('пустой ответ → [] и totalCount=0', () => {
    const { items, totalCount } = parseListViewResponse<FooRaw, FooItem>({}, toFoo);
    expect(items.length).toBe(0);
    expect(totalCount).toBe(0);
  });

  it('Data приоритетнее Items, Total приоритетнее TotalCount', () => {
    const raw: ListViewResponseRaw<FooRaw> = {
      Data: [{ ID: 1, Name: 'from Data' }],
      Items: [{ ID: 2, Name: 'from Items' }],
      Total: 1,
      TotalCount: 999,
    };
    const { items, totalCount } = parseListViewResponse(raw, toFoo);
    expect(items[0].name).toBe('from Data');
    expect(totalCount).toBe(1);
  });

  it('totalCount по умолчанию = items.length, если не передан', () => {
    const raw: ListViewResponseRaw<FooRaw> = {
      Data: [{ ID: 1 }, { ID: 2 }, { ID: 3 }],
    };
    const { items, totalCount } = parseListViewResponse(raw, toFoo);
    expect(items.length).toBe(3);
    expect(totalCount).toBe(3);
  });
});
