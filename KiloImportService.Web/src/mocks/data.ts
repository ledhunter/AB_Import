import type { SiteItem } from '../types/listView';
import type { ImportReport, ImportProgress } from '../types/import';

// MOCK_PROJECTS удалён — список проектов теперь грузится из Visary API
// через useProjects() (см. doc_project/08-visary-api-integration.md).
//
// ⚠️ MOCK_SITES остаётся временно — но ключи (1, 2, 3) не совпадают
// с реальными ID проектов из Visary. После выбора реального проекта список
// объектов будет ПУСТОЙ. Заменить на useSites(projectId), когда появится
// endpoint /listview/constructionsite.
export const MOCK_SITES: Record<number, SiteItem[]> = {
  1: [
    { id: 101, title: 'Корпус 1', constructionPermissionNumber: 'РНС-001', constructionProjectNumber: 'НПС-001', stageNumber: 'Этап 1' },
    { id: 102, title: 'Корпус 2', constructionPermissionNumber: 'РНС-002', constructionProjectNumber: 'НПС-001', stageNumber: 'Этап 1' },
  ],
  2: [
    { id: 201, title: 'Корпус А', constructionPermissionNumber: 'РНС-010', constructionProjectNumber: 'НПС-005', stageNumber: 'Этап 1' },
  ],
  3: [
    { id: 301, title: 'Башня 1', constructionPermissionNumber: 'РНС-020', constructionProjectNumber: 'НПС-010', stageNumber: 'Этап 1' },
    { id: 302, title: 'Башня 2', constructionPermissionNumber: 'РНС-021', constructionProjectNumber: 'НПС-010', stageNumber: 'Этап 2' },
    { id: 303, title: 'Паркинг', constructionPermissionNumber: 'РНС-022', constructionProjectNumber: 'НПС-010', stageNumber: 'Этап 1' },
  ],
  4: [],
};

export const MOCK_PROGRESS: ImportProgress = {
  importId: 'abc-123',
  currentRow: 67,
  totalRows: 150,
  currentSheet: 'Реестр помещений',
  percentComplete: 45,
};

export const MOCK_REPORT: ImportReport = {
  importId: 'abc-123',
  status: 'completedWithWarnings',
  importType: 'mixed',
  fileFormat: 'xlsx',
  fileName: 'import_2024-Q1.xlsx',
  startedAt: '2024-03-15T10:00:00Z',
  completedAt: '2024-03-15T10:02:35Z',
  duration: '00:02:35',
  summary: {
    totalSheets: 2,
    totalRows: 150,
    roomsCreated: 72,
    roomsUpdated: 18,
    roomsSkipped: 5,
    shareAgreementsCreated: 60,
    shareAgreementsUpdated: 12,
    shareAgreementsSkipped: 3,
    errorsCount: 4,
    warningsCount: 8,
  },
  rows: [
    {
      rowNumber: 1,
      sheet: 'Реестр помещений',
      status: 'success',
      sourceData: { roomNumber: '101', rns: 'РНС-001', nps: 'НПС-001', stage: 'Этап 1', floor: '1', section: 'Секция 1' },
      destinations: [
        { entity: 'Room', action: 'Created', entityId: 789, entityTitle: 'Помещение 101', targetField: 'ConstructionSite → Section → Room' },
        { entity: 'ShareAgreement', action: 'Created', entityId: 1024, entityTitle: 'ДДУ-001 от 01.01.2024', targetField: 'Room → ShareAgreement' },
      ],
      warnings: [],
      errors: [],
    },
    {
      rowNumber: 2,
      sheet: 'Реестр помещений',
      status: 'success',
      sourceData: { roomNumber: '102', rns: 'РНС-001', nps: 'НПС-001', stage: 'Этап 1', floor: '1', section: 'Секция 1' },
      destinations: [
        { entity: 'Room', action: 'Updated', entityId: 790, entityTitle: 'Помещение 102', targetField: 'ConstructionSite → Section → Room' },
        { entity: 'ShareAgreement', action: 'Created', entityId: 1025, entityTitle: 'ДДУ-002 от 05.01.2024', targetField: 'Room → ShareAgreement' },
      ],
      warnings: [],
      errors: [],
    },
    {
      rowNumber: 3,
      sheet: 'Реестр помещений',
      status: 'warning',
      sourceData: { roomNumber: '103', rns: '', nps: 'НПС-001', stage: 'Этап 1', floor: '1', section: 'Секция 1' },
      destinations: [
        { entity: 'Room', action: 'Created', entityId: 791, entityTitle: 'Помещение 103', targetField: 'ConstructionSite → Section → Room' },
      ],
      warnings: [
        { field: 'rns', message: "Поле 'РНС' пустое, поиск по альтернативным полям (НПС + Этап)" },
      ],
      errors: [],
    },
    {
      rowNumber: 10,
      sheet: 'Реестр помещений',
      status: 'warning',
      sourceData: { roomNumber: '201', rns: 'РНС-001', nps: 'НПС-001', stage: 'Этап 1', floor: '2', section: '' },
      destinations: [
        { entity: 'Room', action: 'Created', entityId: 800, entityTitle: 'Помещение 201', targetField: 'ConstructionSite → Section → Room' },
      ],
      warnings: [
        { field: 'section', message: "Поле 'Подъезд/Секция' пустое, использована секция по умолчанию" },
      ],
      errors: [],
    },
    {
      rowNumber: 25,
      sheet: 'Реестр помещений',
      status: 'error',
      sourceData: { roomNumber: '205', rns: '', nps: 'НПС-999', stage: 'Этап 1', floor: '2', section: 'Секция 3' },
      destinations: [],
      warnings: [
        { field: 'rns', message: "Поле 'РНС' пустое, поиск по альтернативным полям" },
      ],
      errors: [
        { field: 'constructionSite', message: 'Не найден объект строительства по НПС-999 + Этап 1' },
      ],
    },
    {
      rowNumber: 42,
      sheet: 'Реестр ДДУ',
      status: 'error',
      sourceData: { roomNumber: '', rns: 'РНС-001', nps: 'НПС-001', stage: 'Этап 1', dduNumber: 'ДДУ-050' },
      destinations: [],
      warnings: [],
      errors: [
        { field: 'roomNumber', message: "Обязательное поле 'Номер помещения' пустое" },
      ],
    },
    {
      rowNumber: 50,
      sheet: 'Реестр ДДУ',
      status: 'success',
      sourceData: { roomNumber: '301', rns: 'РНС-001', nps: 'НПС-001', stage: 'Этап 1', dduNumber: 'ДДУ-051', dduDate: '15.02.2024' },
      destinations: [
        { entity: 'ShareAgreement', action: 'Updated', entityId: 1100, entityTitle: 'ДДУ-051 от 15.02.2024', targetField: 'Room → ShareAgreement' },
      ],
      warnings: [],
      errors: [],
    },
    {
      rowNumber: 75,
      sheet: 'Реестр ДДУ',
      status: 'error',
      sourceData: { roomNumber: '999', rns: 'РНС-001', nps: 'НПС-001', stage: 'Этап 1', dduNumber: 'ДДУ-099' },
      destinations: [],
      warnings: [],
      errors: [
        { field: 'room', message: "Помещение 999 не найдено в секции" },
      ],
    },
  ],
  pagination: {
    page: 1,
    pageSize: 50,
    totalPages: 3,
    totalItems: 150,
  },
};
