import type { ImportTypeOption } from '../types/import';

/**
 * Реестр доступных типов импорта.
 * Список будет расширяться по мере добавления новых сценариев.
 * В будущем подгружается через `GET /api/import/types`.
 */
export const IMPORT_TYPES: ImportTypeOption[] = [
  { id: 'rooms', label: 'Помещения', description: 'Импорт реестра помещений (квартир, машиномест, кладовых)' },
  { id: 'shareAgreements', label: 'ДДУ', description: 'Импорт договоров долевого участия' },
  { id: 'mixed', label: 'Помещения + ДДУ (полный цикл)', description: 'Совместный импорт помещений и связанных ДДУ' },
  { id: 'paymentSchedule', label: 'График платежей по ДДУ', description: 'Импорт графика платежей' },
  { id: 'escrowAccounts', label: 'Счета эскроу', description: 'Импорт данных по счетам эскроу' },
  { id: 'constructionSites', label: 'Объекты строительства', description: 'Импорт объектов и секций' },
  { id: 'organizations', label: 'Организации (застройщики)', description: 'Импорт справочника организаций' },
  { id: 'buyers', label: 'Покупатели (физ.лица)', description: 'Импорт покупателей по ДДУ' },
];
