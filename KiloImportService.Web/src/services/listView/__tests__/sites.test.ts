/**
 * Тесты маппера и query-хелпера для объектов строительства.
 */
import { describe, expect, it } from 'vitest';
import { buildSitesQueryByProject, toSiteItem } from '../entities/sites';
import type { ConstructionSiteRaw } from '../../../types/listView';

describe('toSiteItem', () => {
  it('все поля заполнены → точное соответствие', () => {
    const raw: ConstructionSiteRaw = {
      ID: 1,
      Title: 'Корпус 5',
      Address: 'ул. Ленина, 10',
      ConstructionProjectNumber: 'CPN-77',
      Type: 'Жилой',
      TotalArea: 1500,
    };
    const item = toSiteItem(raw);
    expect(item.id).toBe(1);
    expect(item.title).toBe('Корпус 5');
    expect(item.address).toBe('ул. Ленина, 10');
    expect(item.constructionProjectNumber).toBe('CPN-77');
    expect(item.type).toBe('Жилой');
    expect(item.totalArea).toBe(1500);
    expect(item.raw).toBe(raw);
  });

  it('пустой Title → "Объект #{id}"', () => {
    const item = toSiteItem({ ID: 5, Title: '' });
    expect(item.title).toBe('Объект #5');
  });

  it('undefined Title → "Объект #{id}"', () => {
    const item = toSiteItem({ ID: 9 });
    expect(item.title).toBe('Объект #9');
  });

  it('пустые опциональные строки → пустые строки в UI-объекте (не undefined)', () => {
    const item = toSiteItem({
      ID: 3,
      Title: 'X',
      Address: null,
      ConstructionProjectNumber: undefined,
      Type: '',
    });
    expect(item.address).toBe('');
    expect(item.constructionProjectNumber).toBe('');
    expect(item.type).toBe('');
  });
});

describe('buildSitesQueryByProject', () => {
  it('формирует AssociationFilter', () => {
    const q = buildSitesQueryByProject(123);
    expect(q.associationFilter).toEqual({
      AssociatedId: 123,
      Filters: null,
    });
  });

  it('сохраняет переданные пагинационные параметры', () => {
    const q = buildSitesQueryByProject(7, { pageSkip: 50, pageSize: 25, searchString: 'foo' });
    expect(q.pageSkip).toBe(50);
    expect(q.pageSize).toBe(25);
    expect(q.searchString).toBe('foo');
    expect(q.associationFilter).toEqual({
      AssociatedId: 7,
      Filters: null,
    });
  });
});
