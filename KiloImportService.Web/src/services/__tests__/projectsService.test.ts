/**
 * Простые unit-тесты для маппинга projectsService.
 *
 * Не проверяет реальный API (это проверяется руками через UI с реальным токеном),
 * но фиксирует контракт нормализации.
 */
import { describe, expect, it } from 'vitest';
import { parseProjectsResponse, toProjectItem } from '../projectsService';
import type { ConstructionProjectRaw, ListViewResponse } from '../../types/listView';

describe('toProjectItem', () => {
  it('Title и IdentifierKK заполнены', () => {
    const raw: ConstructionProjectRaw = {
      ID: 42,
      Title: 'ЖК Алые Паруса',
      IdentifierKK: 'KK-001',
      IdentifierZPLM: 'ZPLM-001',
    };
    const item = toProjectItem(raw);
    expect(item.id).toBe(42);
    expect(item.title).toBe('ЖК Алые Паруса');
    expect(item.raw).toBe(raw);
  });

  it('IdentifierKK пустой → fallback на IdentifierZPLM', () => {
    const raw: ConstructionProjectRaw = {
      ID: 7,
      Title: 'Проект Б',
      IdentifierKK: null,
      IdentifierZPLM: 'ZPLM-555',
    };
    const item = toProjectItem(raw);
    // code удален — проверяем только title
    expect(item.title).toBe('Проект Б');
  });

  it('оба идентификатора пустые → fallback на ID-{id}', () => {
    const raw: ConstructionProjectRaw = {
      ID: 99,
      Title: 'Проект В',
      IdentifierKK: null,
      IdentifierZPLM: null,
    };
    const item = toProjectItem(raw);
    // code удален — проверяем только title
    expect(item.title).toBe('Проект В');
  });

  it('пустой Title → "Проект #{id}"', () => {
    const raw = {
      ID: 5,
      Title: '',
      IdentifierKK: 'X',
    } as ConstructionProjectRaw;
    const item = toProjectItem(raw);
    expect(item.title).toBe('Проект #5');
  });

  it('undefined Title (опциональное поле) → "Проект #{id}"', () => {
    // эмулируем неполный JSON от backend
    const raw = { ID: 11 } as ConstructionProjectRaw;
    const item = toProjectItem(raw);
    expect(item.title).toBe('Проект #11');
    // code удален — проверяем только title
  });
});

describe('parseProjectsResponse', () => {
  it('реальный формат Visary { Data, Total, Summaries }', () => {
    const response: ListViewResponse<ConstructionProjectRaw> = {
      Data: [
        { ID: 1, Title: 'Project A', IdentifierKK: 'KK-1' },
        { ID: 2, Title: 'Project B', IdentifierKK: 'KK-2' },
      ],
      Total: 2387,
      Summaries: [],
    };
    const { items, totalCount } = parseProjectsResponse(response);
    expect(items.length).toBe(2);
    expect(items[0].id).toBe(1);
    expect(items[0].title).toBe('Project A');
    expect(totalCount).toBe(2387);
  });

  it('формат-fallback { Items, TotalCount }', () => {
    const response: ListViewResponse<ConstructionProjectRaw> = {
      Items: [{ ID: 5, Title: 'P5' }],
      TotalCount: 100,
    };
    const { items, totalCount } = parseProjectsResponse(response);
    expect(items.length).toBe(1);
    expect(totalCount).toBe(100);
  });

  it('формат-fallback camelCase { items, totalCount }', () => {
    const response: ListViewResponse<ConstructionProjectRaw> = {
      items: [{ ID: 7, Title: 'P7' }],
      totalCount: 7,
    };
    const { items, totalCount } = parseProjectsResponse(response);
    expect(items.length).toBe(1);
    expect(totalCount).toBe(7);
  });

  it('пустой ответ → пустой массив, totalCount=0', () => {
    const { items, totalCount } = parseProjectsResponse({});
    expect(items.length).toBe(0);
    expect(totalCount).toBe(0);
  });

  it('Data приоритетнее Items (если оба есть)', () => {
    const response: ListViewResponse<ConstructionProjectRaw> = {
      Data: [{ ID: 1, Title: 'from Data' }],
      Items: [{ ID: 2, Title: 'from Items' }],
      Total: 1,
      TotalCount: 999,
    };
    const { items, totalCount } = parseProjectsResponse(response);
    expect(items.length).toBe(1);
    expect(items[0].title).toBe('from Data');
    expect(totalCount).toBe(1);
  });
});
