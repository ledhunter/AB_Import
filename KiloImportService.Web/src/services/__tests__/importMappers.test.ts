/**
 * Unit-тесты мапперов из API-DTO в UI-типы.
 *
 * Запуск: npx tsx src/services/__tests__/importMappers.test.ts
 *
 * Без зависимостей от React/DOM — чистая логика. `process` объявлен локально
 * (этот файл исключён из tsconfig.app.json и собирается отдельно через tsx).
 */

declare const process: { exit(code: number): never };

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

// ───────── Минимальный test-runner (как в других __tests__ проекта) ─────────
let passed = 0;
let failed = 0;
function test(name: string, fn: () => void) {
  try {
    fn();
    console.log(`  ✓ ${name}`);
    passed++;
  } catch (err) {
    console.error(`  ✗ ${name}`);
    console.error(`    ${(err as Error).message}`);
    failed++;
  }
}
function assertEqual<T>(actual: T, expected: T, msg = '') {
  const a = JSON.stringify(actual);
  const e = JSON.stringify(expected);
  if (a !== e) {
    throw new Error(`${msg}\n  expected: ${e}\n  actual:   ${a}`);
  }
}

// ───────── toSessionVariant ─────────
console.log('toSessionVariant:');
test('Pending → pending', () => assertEqual(toSessionVariant('Pending'), 'pending'));
test('Parsing/Validating/Applying → progress', () => {
  assertEqual(toSessionVariant('Parsing'), 'progress');
  assertEqual(toSessionVariant('Validating'), 'progress');
  assertEqual(toSessionVariant('Applying'), 'progress');
});
test('Validated → awaiting', () => assertEqual(toSessionVariant('Validated'), 'awaiting'));
test('Applied → success', () => assertEqual(toSessionVariant('Applied'), 'success'));
test('Failed → failed', () => assertEqual(toSessionVariant('Failed'), 'failed'));
test('Cancelled → cancelled', () => assertEqual(toSessionVariant('Cancelled'), 'cancelled'));

// ───────── computeDuration ─────────
console.log('\ncomputeDuration:');
test('null completedAt → null', () =>
  assertEqual(computeDuration('2026-04-29T11:00:00Z', null), null));
test('одинаковые → 00:00:00', () =>
  assertEqual(
    computeDuration('2026-04-29T11:00:00Z', '2026-04-29T11:00:00Z'),
    '00:00:00',
  ));
test('2 минуты 35 секунд → 00:02:35', () =>
  assertEqual(
    computeDuration('2026-04-29T11:00:00Z', '2026-04-29T11:02:35Z'),
    '00:02:35',
  ));
test('1 час → 01:00:00', () =>
  assertEqual(
    computeDuration('2026-04-29T11:00:00Z', '2026-04-29T12:00:00Z'),
    '01:00:00',
  ));
test('completedAt < startedAt → null', () =>
  assertEqual(
    computeDuration('2026-04-29T12:00:00Z', '2026-04-29T11:00:00Z'),
    null,
  ));
test('некорректные даты → null', () =>
  assertEqual(computeDuration('not-a-date', '2026-04-29T11:00:00Z'), null));

// ───────── toUiSession ─────────
console.log('\ntoUiSession:');
test('базовый кейс — все поля прокидываются', () => {
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
  assertEqual(ui.sessionId, 'abc-123');
  assertEqual(ui.fileFormat, 'xlsx', 'fileFormat должен быть в lowercase');
  assertEqual(ui.variant, 'awaiting');
  assertEqual(ui.duration, '00:02:35');
  assertEqual(ui.stages.length, 1);
});

test('Pending без completedAt → duration null', () => {
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
  assertEqual(ui.duration, null);
  assertEqual(ui.variant, 'pending');
});

test('пустой fileFormat не падает', () => {
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
  assertEqual(ui.fileFormat, '');
});

// ───────── toUiRowError ─────────
console.log('\ntoUiRowError:');
test('маппинг полей сохраняется', () => {
  const api: ApiImportError = {
    sourceRowNumber: 5,
    columnName: 'Площадь',
    errorCode: 'invalid_number',
    message: '"abc" не число',
  };
  const ui = toUiRowError(api);
  assertEqual(ui, {
    rowNumber: 5,
    columnName: 'Площадь',
    errorCode: 'invalid_number',
    message: '"abc" не число',
  });
});

// ───────── toUiReport ─────────
console.log('\ntoUiReport:');
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

test('ошибки группируются по rowNumber', () => {
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
  assertEqual(ui.rows.length, 3);
  assertEqual(ui.rows[0].rowNumber, 2);
  assertEqual(ui.rows[0].errors.length, 0);
  assertEqual(ui.rows[1].rowNumber, 3);
  assertEqual(ui.rows[1].errors.length, 1);
  assertEqual(ui.rows[2].rowNumber, 4);
  assertEqual(ui.rows[2].errors.length, 2);
  assertEqual(ui.fileLevelErrors.length, 0);
});

test('file-level ошибки (rowNumber=0) попадают в fileLevelErrors', () => {
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
  assertEqual(ui.fileLevelErrors.length, 2);
  assertEqual(ui.rows.length, 0);
});

test('осиротевшая ошибка (rowNumber>0, нет в rows) добавляется как Invalid-ряд', () => {
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
  assertEqual(ui.rows.length, 2);
  assertEqual(ui.rows[0].rowNumber, 5);
  assertEqual(ui.rows[1].rowNumber, 9);
  assertEqual(ui.rows[1].status, 'Invalid');
});

test('session: status/variant/totalRows из api перезаписывают переданный snapshot', () => {
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
  assertEqual(ui.session.status, 'Applied');
  assertEqual(ui.session.variant, 'success');
  assertEqual(ui.session.totalRows, 10);
});

test('пустые rows/errors → пустой UI-отчёт', () => {
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
  assertEqual(ui.rows.length, 0);
  assertEqual(ui.fileLevelErrors.length, 0);
});

// ───────── Итог ─────────
console.log(`\n${passed} passed, ${failed} failed`);
if (failed > 0) process.exit(1);
