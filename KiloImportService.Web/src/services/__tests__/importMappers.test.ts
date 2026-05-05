/**
 * Unit-тесты мапперов из API-DTO в UI-типы.
 *
 * Без зависимостей от React/DOM — чистая логика.
 */
import { describe, expect, it } from 'vitest';
import {
  computeDuration,
  toSessionVariant,
  toUiReport,
  toUiRowError,
  toUiSession,
} from '../importMappers';
import type {
  ApiImportError,
  ApiImportReport,
  ApiImportSession,
} from '../../types/api';
import type { UiSession } from '../../types/session';

describe('toSessionVariant', () => {
  it('Pending → pending', () => {
    expect(toSessionVariant('Pending')).toBe('pending');
  });

  it('Parsing/Validating/Applying → progress', () => {
    expect(toSessionVariant('Parsing')).toBe('progress');
    expect(toSessionVariant('Validating')).toBe('progress');
    expect(toSessionVariant('Applying')).toBe('progress');
  });

  it('Validated → awaiting', () => {
    expect(toSessionVariant('Validated')).toBe('awaiting');
  });

  it('Applied → success', () => {
    expect(toSessionVariant('Applied')).toBe('success');
  });

  it('Failed → failed', () => {
    expect(toSessionVariant('Failed')).toBe('failed');
  });

  it('Cancelled → cancelled', () => {
    expect(toSessionVariant('Cancelled')).toBe('cancelled');
  });
});

describe('computeDuration', () => {
  it('null completedAt → null', () => {
    expect(computeDuration('2026-04-29T11:00:00Z', null)).toBe(null);
  });

  it('одинаковые → 00:00:00', () => {
    expect(computeDuration('2026-04-29T11:00:00Z', '2026-04-29T11:00:00Z')).toBe('00:00:00');
  });

  it('2 минуты 35 секунд → 00:02:35', () => {
    expect(computeDuration('2026-04-29T11:00:00Z', '2026-04-29T11:02:35Z')).toBe('00:02:35');
  });

  it('1 час → 01:00:00', () => {
    expect(computeDuration('2026-04-29T11:00:00Z', '2026-04-29T12:00:00Z')).toBe('01:00:00');
  });

  it('completedAt < startedAt → null', () => {
    expect(computeDuration('2026-04-29T12:00:00Z', '2026-04-29T11:00:00Z')).toBe(null);
  });

  it('некорректные даты → null', () => {
    expect(computeDuration('not-a-date', '2026-04-29T11:00:00Z')).toBe(null);
  });
});

describe('toUiSession', () => {
  it('базовый кейс — все поля прокидываются', () => {
    const api: ApiImportSession = {
      sessionId: 'abc-123',
      importTypeCode: 'rooms',
      fileName: 'data.xlsx',
      fileFormat: 'Xlsx',
      status: 'Validated',
      startedAt: '2026-04-29T11:00:00Z',
      completedAt: '2026-04-29T11:02:35Z',
      totalRows: 100,
      successRows: 90,
      errorRows: 10,
      errorMessage: null,
      stages: [
        {
          kind: 'Parse',
          startedAt: '2026-04-29T11:00:00Z',
          completedAt: '2026-04-29T11:00:30Z',
          isSuccess: true,
          progressPercent: 100,
          message: 'Прочитано: 100',
        },
      ],
    };
    const ui = toUiSession(api);
    expect(ui.sessionId).toBe('abc-123');
    expect(ui.fileFormat).toBe('xlsx');
    expect(ui.variant).toBe('awaiting');
    expect(ui.duration).toBe('00:02:35');
    expect(ui.stages.length).toBe(1);
  });

  it('Pending без completedAt → duration null', () => {
    const api: ApiImportSession = {
      sessionId: 'x',
      importTypeCode: 'rooms',
      fileName: 'f.csv',
      fileFormat: 'Csv',
      status: 'Pending',
      startedAt: '2026-04-29T11:00:00Z',
      completedAt: null,
      totalRows: 0,
      successRows: 0,
      errorRows: 0,
      errorMessage: null,
      stages: [],
    };
    const ui = toUiSession(api);
    expect(ui.duration).toBe(null);
    expect(ui.variant).toBe('pending');
  });

  it('пустой fileFormat не падает', () => {
    const api: ApiImportSession = {
      sessionId: 'x',
      importTypeCode: 'rooms',
      fileName: 'f',
      fileFormat: '' as never,
      status: 'Pending',
      startedAt: '2026-04-29T11:00:00Z',
      completedAt: null,
      totalRows: 0,
      successRows: 0,
      errorRows: 0,
      errorMessage: null,
      stages: [],
    };
    const ui = toUiSession(api);
    expect(ui.fileFormat).toBe('');
  });
});

describe('toUiRowError', () => {
  it('маппинг полей сохраняется', () => {
    const api: ApiImportError = {
      sourceRowNumber: 5,
      columnName: 'Площадь',
      errorCode: 'invalid_number',
      message: '"abc" не число',
    };
    const ui = toUiRowError(api);
    expect(ui).toEqual({
      rowNumber: 5,
      columnName: 'Площадь',
      errorCode: 'invalid_number',
      message: '"abc" не число',
    });
  });
});

describe('toUiReport', () => {
  const baseSession: UiSession = {
    sessionId: 'abc',
    importTypeCode: 'rooms',
    fileName: 'data.xlsx',
    fileFormat: 'xlsx',
    status: 'Validated',
    variant: 'awaiting',
    startedAt: '2026-04-29T11:00:00Z',
    completedAt: null,
    duration: null,
    totalRows: 0,
    successRows: 0,
    errorRows: 0,
    errorMessage: null,
    stages: [],
    stageProgress: null,
  };

  it('ошибки группируются по rowNumber', () => {
    const api: ApiImportReport = {
      sessionId: 'abc',
      status: 'Validated',
      totalRows: 3,
      successRows: 1,
      errorRows: 2,
      rows: [
        { sourceRowNumber: 2, status: 'Valid' },
        { sourceRowNumber: 3, status: 'Invalid' },
        { sourceRowNumber: 4, status: 'Invalid' },
      ],
      errors: [
        { sourceRowNumber: 3, columnName: 'A', errorCode: 'e1', message: 'm1' },
        { sourceRowNumber: 4, columnName: 'B', errorCode: 'e2', message: 'm2' },
        { sourceRowNumber: 4, columnName: 'C', errorCode: 'e3', message: 'm3' },
      ],
      rowsPagination: { skip: 0, take: 100, total: 3 },
    };
    const ui = toUiReport(api, baseSession);
    expect(ui.rows.length).toBe(3);
    expect(ui.rows[0].rowNumber).toBe(2);
    expect(ui.rows[0].errors.length).toBe(0);
    expect(ui.rows[1].rowNumber).toBe(3);
    expect(ui.rows[1].errors.length).toBe(1);
    expect(ui.rows[2].rowNumber).toBe(4);
    expect(ui.rows[2].errors.length).toBe(2);
    expect(ui.fileLevelErrors.length).toBe(0);
  });

  it('file-level ошибки (rowNumber=0) попадают в fileLevelErrors', () => {
    const api: ApiImportReport = {
      sessionId: 'abc',
      status: 'Failed',
      totalRows: 0,
      successRows: 0,
      errorRows: 0,
      rows: [],
      errors: [
        { sourceRowNumber: 0, columnName: null, errorCode: 'site_required', message: 'Нет site' },
        { sourceRowNumber: 0, columnName: null, errorCode: 'parse_failure', message: 'Bad XLSX' },
      ],
      rowsPagination: { skip: 0, take: 100, total: 0 },
    };
    const ui = toUiReport(api, baseSession);
    expect(ui.fileLevelErrors.length).toBe(2);
    expect(ui.rows.length).toBe(0);
  });

  it('осиротевшая ошибка (rowNumber>0, нет в rows) добавляется как Invalid-ряд', () => {
    const api: ApiImportReport = {
      sessionId: 'abc',
      status: 'Failed',
      totalRows: 1,
      successRows: 0,
      errorRows: 1,
      rows: [{ sourceRowNumber: 5, status: 'Valid' }],
      errors: [
        { sourceRowNumber: 9, columnName: 'X', errorCode: 'orphan', message: 'orphan-err' },
      ],
      rowsPagination: { skip: 0, take: 100, total: 1 },
    };
    const ui = toUiReport(api, baseSession);
    expect(ui.rows.length).toBe(2);
    expect(ui.rows[0].rowNumber).toBe(5);
    expect(ui.rows[1].rowNumber).toBe(9);
    expect(ui.rows[1].status).toBe('Invalid');
  });

  it('session: status/variant/totalRows из api перезаписывают переданный snapshot', () => {
    const api: ApiImportReport = {
      sessionId: 'abc',
      status: 'Applied',
      totalRows: 10,
      successRows: 10,
      errorRows: 0,
      rows: [],
      errors: [],
      rowsPagination: { skip: 0, take: 100, total: 0 },
    };
    const ui = toUiReport(api, { ...baseSession, status: 'Validated', variant: 'awaiting' });
    expect(ui.session.status).toBe('Applied');
    expect(ui.session.variant).toBe('success');
    expect(ui.session.totalRows).toBe(10);
  });

  it('пустые rows/errors → пустой UI-отчёт', () => {
    const api: ApiImportReport = {
      sessionId: 'abc',
      status: 'Pending',
      totalRows: 0,
      successRows: 0,
      errorRows: 0,
      rows: [],
      errors: [],
      rowsPagination: { skip: 0, take: 100, total: 0 },
    };
    const ui = toUiReport(api, baseSession);
    expect(ui.rows.length).toBe(0);
    expect(ui.fileLevelErrors.length).toBe(0);
  });
});
