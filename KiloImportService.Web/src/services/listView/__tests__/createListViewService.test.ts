/**
 * Тесты на сборку тела запроса Visary ListView.
 *
 * Сетевая часть (`fetch`) НЕ тестируется здесь — она зависит от import.meta.env Vite
 * и проверяется руками через UI с реальным токеном (см. doc_project/08-visary-api-integration.md).
 */
import { describe, expect, it } from 'vitest';
import { buildListViewRequestBody } from '../createListViewService';
import type { ListViewServiceConfig } from '../types';

interface FooRaw { ID: number }
interface FooItem { id: number }

const baseConfig: ListViewServiceConfig<FooRaw, FooItem> = {
  mnemonic: 'foo',
  columns: ['ID', 'Name'],
  toItem: (r) => ({ id: r.ID }),
};

describe('buildListViewRequestBody', () => {
  it('дефолты при пустом query', () => {
    const body = buildListViewRequestBody(baseConfig, {});
    expect(body.Mnemonic).toBe('foo');
    expect(body.Columns).toEqual(['ID', 'Name']);
    expect(body.PageSkip).toBe(0);
    expect(body.PageSize).toBe(50);
    expect(body.Sorts).toBe('[{"selector":"ID","desc":true}]');
    expect(body.Hidden).toBe(false);
    expect(body.ExtraFilter).toBe(null);
    expect(body.SearchPhrase).toBe(null);
    expect(body.Summaries).toEqual([]);
  });

  it('query переопределяет дефолты', () => {
    const body = buildListViewRequestBody(baseConfig, {
      pageSkip: 100,
      pageSize: 25,
      searchString: 'abc',
      extraFilter: '[["X","=",1]]',
      sorts: '[{"selector":"Title","desc":false}]',
    });
    expect(body.PageSkip).toBe(100);
    expect(body.PageSize).toBe(25);
    expect(body.SearchPhrase).toBe('abc');
    expect(body.ExtraFilter).toBe('[["X","=",1]]');
    expect(body.Sorts).toBe('[{"selector":"Title","desc":false}]');
  });

  it('defaultPageSize из конфига применяется, если query.pageSize не задан', () => {
    const body = buildListViewRequestBody(
      { ...baseConfig, defaultPageSize: 200 },
      {},
    );
    expect(body.PageSize).toBe(200);
  });

  it('query.pageSize важнее config.defaultPageSize', () => {
    const body = buildListViewRequestBody(
      { ...baseConfig, defaultPageSize: 200 },
      { pageSize: 10 },
    );
    expect(body.PageSize).toBe(10);
  });

  it('defaultSort из конфига применяется, если query.sorts не задан', () => {
    const body = buildListViewRequestBody(
      { ...baseConfig, defaultSort: '[{"selector":"Created","desc":true}]' },
      {},
    );
    expect(body.Sorts).toBe('[{"selector":"Created","desc":true}]');
  });

  it('signal не попадает в тело запроса', () => {
    const controller = new AbortController();
    const body = buildListViewRequestBody(baseConfig, { signal: controller.signal });
    expect('signal' in body).toBe(false);
  });

  it('AssociationFilter НЕ попадает в тело для /onetomany эндпоинтов', () => {
    const configWithOnetomany = {
      ...baseConfig,
      pathSuffix: '/onetomany/Project',
    };
    const body = buildListViewRequestBody(configWithOnetomany, {
      associationFilter: { AssociatedId: 123, Filters: null },
    });
    expect('AssociationFilter' in body).toBe(false);
  });

  it('AssociationFilter попадает в тело для обычных эндпоинтов', () => {
    const body = buildListViewRequestBody(baseConfig, {
      associationFilter: { AssociatedId: 456, Filters: null },
    });
    expect(body.AssociationFilter).toEqual({ AssociatedId: 456, Filters: null });
  });
});
