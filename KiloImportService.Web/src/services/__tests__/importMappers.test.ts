import { describe, expect, it } from 'vitest';
import { computeDuration, toSessionVariant } from '../importMappers.ts';
import type { ApiImportStatus } from '../../types/api';
import type { SessionStatusVariant } from '../../types/session';

describe('toSessionVariant', () => {
  const cases: Array<[ApiImportStatus, SessionStatusVariant]> = [
    ['Pending', 'pending'],
    ['Parsing', 'progress'],
    ['Validating', 'progress'],
    ['Applying', 'progress'],
    ['Validated', 'awaiting'],
    ['Applied', 'success'],
    ['Failed', 'failed'],
    ['Cancelled', 'cancelled'],
  ];

  it('маппит все статусы правильно', () => {
    for (const [status, expected] of cases) {
      expect(toSessionVariant(status)).toBe(expected);
    }
  });

  it('default case возвращает pending', () => {
    const unknownStatus = 'UnknownStatus' as ApiImportStatus;
    expect(toSessionVariant(unknownStatus)).toBe('pending');
  });
});

describe('computeDuration', () => {
  it('возвращает null при отсутствии completedAt', () => {
    expect(computeDuration('2026-04-29T11:00:00Z', null)).toBe(null);
  });

  it('возвращает 00:00:00 при одинаковых датах', () => {
    expect(computeDuration('2026-04-29T11:00:00Z', '2026-04-29T11:00:00Z')).toBe('00:00:00');
  });

  it('вычисляет длительность 2 минуты 35 секунд', () => {
    expect(computeDuration('2026-04-29T11:00:00Z', '2026-04-29T11:02:35Z')).toBe('00:02:35');
  });

  it('вычисляет длительность 1 час', () => {
    expect(computeDuration('2026-04-29T11:00:00Z', '2026-04-29T12:00:00Z')).toBe('01:00:00');
  });

  it('возвращает null при completedAt < startedAt', () => {
    expect(computeDuration('2026-04-29T12:00:00Z', '2026-04-29T11:00:00Z')).toBe(null);
  });

  it('возвращает null при некорректных датах', () => {
    expect(computeDuration('not-a-date', '2026-04-29T11:00:00Z')).toBe(null);
    expect(computeDuration('2026-04-29T11:00:00Z', 'not-a-date')).toBe(null);
  });

  it('работает с миллисекундами в ISO', () => {
    expect(computeDuration('2026-04-29T11:00:00.000Z', '2026-04-29T11:00:01.500Z')).toBe('00:00:02');
  });
});

describe('邊界 cases', () => {
  it('работает с пограничными значениями временных интервалов', () => {
    expect(computeDuration('2026-04-29T00:00:00Z', '2026-04-29T00:00:59Z')).toBe('00:00:59');
    expect(computeDuration('2026-04-29T00:00:00Z', '2026-04-29T23:59:59Z')).toBe('23:59:59');
  });
});
