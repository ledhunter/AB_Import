import { useEffect, useMemo, useRef, useState } from 'react';
import { Status } from '@alfalab/core-components/status';
import { Typography } from '@alfalab/core-components/typography';
import { Pagination } from '@alfalab/core-components/pagination';
import type { RowStatus, UiReport, UiReportRow } from '../../types/session';

interface Props {
  report: UiReport;
  /**
   * Колбэк пагинации: вызывается при клике пользователя на номер страницы
   * (0-based — как в Alfa Pagination). Также вызывается при изменении набора
   * свёрнутых листов (`options.excludeSheets`) — backend пересчитает `total`
   * с учётом исключённых листов, чтобы пагинация считалась только по видимым
   * строкам. Если не передан — пагинация отображается, но в read-only режиме.
   */
  onPageChange?: (
    skip: number,
    options?: { excludeSheets?: string[] },
  ) => void;
}

type Filter =
  | 'all'
  | 'invalid'
  | 'valid'
  | 'applied'
  | 'failed'
  | 'created'
  | 'updated'
  | 'skipped';

const ROW_STATUS_LABEL: Record<RowStatus, string> = {
  Pending: 'Ожидает',
  Valid: 'Валидно',
  Invalid: 'Ошибка',
  Applied: 'Применено',
  Failed: 'Не применилось',
};

const ROW_STATUS_COLOR: Record<RowStatus, 'green' | 'red' | 'orange' | 'grey'> = {
  Pending: 'grey',
  Valid: 'green',
  Invalid: 'red',
  Applied: 'green',
  Failed: 'red',
};

/**
 * Action-фильтры — по реальному действию над ГЛАВНОЙ сущностью строки
 * (помещение). Строка может содержать побочные метки («ДДУ создан»,
 * «Корпус найден», «Застройщик переиспользован» …), но при подсчёте
 * нас интересует ровно одно: помещение создано, обновлено или skip-нуто.
 * Иначе фильтры будут пересекаться — строка с PATCH Room + CREATE SA
 * попала бы и в «Созданные», и в «Обновлённые», сумма превысила бы
 * `Applied`. Метки совпадают с теми, что пишет
 * [RoomsFormImportMapper.cs](../../../KiloImportService.Api/Domain/Mapping/RoomsFormImportMapper.cs)
 * — `«Помещение создано (№…)»` / `«Помещение обновлено (№…)»` /
 * `«Без изменений — пропуск (snapshot)»`.
 *
 * Категории взаимоисключающие → сумма counts.created + updated + skipped
 * ≤ Applied (равенство — когда каждая Applied-строка относится к
 * помещению; для not-RoomsForm импортов часть строк может не иметь
 * ни одной из этих меток).
 */
function actionMatchesCreated(actions: readonly string[]): boolean {
  return actions.some((a) => /Помещение создан/i.test(a));
}
function actionMatchesUpdated(actions: readonly string[]): boolean {
  return actions.some((a) => /Помещение обновлен/i.test(a));
}
function actionMatchesSkipped(actions: readonly string[]): boolean {
  // «Без изменений — пропуск (snapshot)» — единственная метка skip-а из
  // RoomApplySnapshotStore diff-hash (doc 96). На случай переименований
  // матчим оба корня — «Без изменений» и «пропуск».
  return actions.some((a) => /Без изменений|пропуск/i.test(a));
}

function matchesFilter(row: UiReportRow, filter: Filter): boolean {
  if (filter === 'all') return true;
  if (filter === 'valid') return row.status === 'Valid' || row.status === 'Applied';
  if (filter === 'invalid') return row.status === 'Invalid';
  if (filter === 'applied') return row.status === 'Applied';
  if (filter === 'failed') return row.status === 'Failed';
  if (filter === 'created') return actionMatchesCreated(row.actions);
  if (filter === 'updated') return actionMatchesUpdated(row.actions);
  if (filter === 'skipped') return actionMatchesSkipped(row.actions);
  return false;
}

export const SessionRowsTable = ({ report, onPageChange }: Props) => {
  const [filter, setFilter] = useState<Filter>('all');
  /**
   * Свёрнутые пользователем листы. Хранятся как Set имён листов (включая
   * специальный ключ `''` для строк без листа). Изменение этого state
   * триггерит перезагрузку отчёта через `onPageChange` с обновлённым
   * `excludeSheets` — иначе пагинация считала бы скрытые строки.
   */
  const [collapsedSheets, setCollapsedSheets] = useState<Set<string>>(
    () => new Set(),
  );

  // Карта всех листов сессии с их полными счётчиками (без учёта excludeSheets).
  // Нужна, чтобы в заголовке свёрнутого листа всё равно показывать "20 стр.".
  const totalsBySheet = useMemo(() => {
    const map = new Map<string, number>();
    for (const t of report.sheetTotals ?? []) {
      map.set(t.sheet ?? '', t.total);
    }
    return map;
  }, [report.sheetTotals]);

  // Все известные листы (для отрисовки заголовков даже свёрнутых),
  // сортируем как сервер: по имени, как в `OrderBy(r => r.Sheet)`.
  const allSheets = useMemo<string[]>(() => {
    const sheets = (report.sheetTotals ?? [])
      .map((t) => t.sheet ?? '')
      .filter((s) => s.length > 0);
    sheets.sort((a, b) => a.localeCompare(b, 'ru'));
    return sheets;
  }, [report.sheetTotals]);

  // Сворачивание разрешено для любого именованного листа — даже единственного.
  // Раньше требовали `>1`, но это лишало пользователя возможности скрыть
  // тысячи строк одного листа (типичный случай файлов «Помещения»: один лист
  // «Квартира» на 6000+ строк). Имя `null` (одностраничный импорт без листов)
  // по-прежнему запрещаем — backend исключение по null не поддерживает.
  const canCollapseAny = allSheets.length >= 1;

  const counts = useMemo(() => {
    // Status-based счётчики — по ВИДИМОЙ странице (как и раньше): backend
    // не агрегирует Invalid/Failed/etc по всей сессии, кроме `successRows`
    // / `errorRows` в карточках сверху. Для action-фильтров есть отдельный
    // session-wide `report.actionTotals` (doc 98 v1.2) — там сервер уже
    // прошёлся по jsonb-меткам всех Applied-строк сессии.
    const byStatus: Record<RowStatus, number> = {
      Pending: 0,
      Valid: 0,
      Invalid: 0,
      Applied: 0,
      Failed: 0,
    };
    for (const r of report.rows) byStatus[r.status]++;
    return {
      all: report.rows.length,
      invalid: byStatus.Invalid,
      valid: byStatus.Valid + byStatus.Applied,
      applied: byStatus.Applied,
      failed: byStatus.Failed,
      created: report.actionTotals.created,
      updated: report.actionTotals.updated,
      skipped: report.actionTotals.skipped,
    };
  }, [report.rows, report.actionTotals]);

  const filtered = useMemo(
    () => report.rows.filter((r) => matchesFilter(r, filter)),
    [report.rows, filter],
  );

  /**
   * Группируем строки текущей страницы по листу. Backend уже исключил
   * свёрнутые листы из выборки — здесь дополнительная фильтрация не нужна,
   * но мы дорисуем «пустые» заголовки для свёрнутых листов, чтобы
   * пользователь видел их и мог развернуть.
   */
  const groups = useMemo(() => {
    const visible: { sheet: string | null; rows: UiReportRow[] }[] = [];
    const indexBySheet = new Map<string, number>();
    for (const r of filtered) {
      const key = r.sheet ?? '';
      const idx = indexBySheet.get(key);
      if (idx === undefined) {
        indexBySheet.set(key, visible.length);
        visible.push({ sheet: r.sheet, rows: [r] });
      } else {
        visible[idx].rows.push(r);
      }
    }
    return visible;
  }, [filtered]);

  // Показывать ли заголовки листов: если есть хотя бы один непустой sheet
  // или строки разнесены по нескольким группам.
  const showSheetHeaders =
    groups.some((g) => g.sheet && g.sheet.length > 0) ||
    groups.length > 1 ||
    allSheets.length > 1;

  // Известные backend'у листы, не присутствующие на текущей странице
  // (например, они либо свёрнуты, либо на другой странице с тем же фильтром).
  // Свёрнутые — рисуем как пустые заголовки с возможностью развернуть.
  const collapsedHeaders = useMemo(
    () =>
      allSheets.filter(
        (sheet) =>
          collapsedSheets.has(sheet) &&
          !groups.some((g) => (g.sheet ?? '') === sheet),
      ),
    [allSheets, collapsedSheets, groups],
  );

  // Идемпотентная отправка excludeSheets в backend при изменении set.
  // Используем ref, чтобы избежать двойной перезагрузки при первом render'е
  // (изначальный state — пустой Set, отправлять excludeSheets=[] не нужно,
  // backend и так отдал бы то же самое).
  const lastSentRef = useRef<string>('');
  useEffect(() => {
    const sorted = Array.from(collapsedSheets).sort();
    const key = sorted.join('|');
    if (key === lastSentRef.current) return;
    lastSentRef.current = key;
    // skip=0 — после изменения списка свёрнутых пагинация теряет смысл,
    // удобнее вернуть пользователя в начало с уже пересчитанным total.
    onPageChange?.(0, { excludeSheets: sorted });
    setFilter('all');
  }, [collapsedSheets, onPageChange]);

  const toggleSheet = (sheet: string) => {
    setCollapsedSheets((prev) => {
      const next = new Set(prev);
      if (next.has(sheet)) next.delete(sheet);
      else next.add(sheet);
      return next;
    });
  };

  return (
    <div className="report-table">
      {report.fileLevelErrors.length > 0 && (
        <div className="messages messages--error" style={{ marginBottom: 16 }}>
          <Typography.Text view="primary-medium" weight="bold" tag="div" style={{ marginBottom: 4 }}>
            Ошибки уровня файла:
          </Typography.Text>
          {report.fileLevelErrors.map((e, i) => (
            <div className="message-row" key={i}>
              <span className="message-field">{e.errorCode}</span>
              <span>{e.message}</span>
            </div>
          ))}
        </div>
      )}

      <div className="filter-tags">
        <FilterButton active={filter === 'all'} count={counts.all} onClick={() => setFilter('all')}>
          Все
        </FilterButton>
        <FilterButton
          active={filter === 'valid'}
          count={counts.valid}
          onClick={() => setFilter('valid')}
        >
          Валидные
        </FilterButton>
        <FilterButton
          active={filter === 'invalid'}
          count={counts.invalid}
          onClick={() => setFilter('invalid')}
        >
          С ошибками
        </FilterButton>
        {counts.applied > 0 && (
          <FilterButton
            active={filter === 'applied'}
            count={counts.applied}
            onClick={() => setFilter('applied')}
          >
            Применённые
          </FilterButton>
        )}
        {counts.failed > 0 && (
          <FilterButton
            active={filter === 'failed'}
            count={counts.failed}
            onClick={() => setFilter('failed')}
          >
            Не применилось
          </FilterButton>
        )}
        {/* Action-фильтры по `row.actions` — что реально произошло с записью
            в Visary. Показываем кнопки только когда такие строки есть на
            текущей странице, иначе пустые табы дезинформируют. См. doc 98. */}
        {counts.created > 0 && (
          <FilterButton
            active={filter === 'created'}
            count={counts.created}
            onClick={() => setFilter('created')}
          >
            Созданные
          </FilterButton>
        )}
        {counts.updated > 0 && (
          <FilterButton
            active={filter === 'updated'}
            count={counts.updated}
            onClick={() => setFilter('updated')}
          >
            Обновлённые
          </FilterButton>
        )}
        {counts.skipped > 0 && (
          <FilterButton
            active={filter === 'skipped'}
            count={counts.skipped}
            onClick={() => setFilter('skipped')}
          >
            Пропущенные
          </FilterButton>
        )}
      </div>

      <div className="table-wrapper">
        <table className="data-table">
          <thead>
            <tr>
              <th style={{ width: 80 }}>№</th>
              <th style={{ width: 160 }}>Статус</th>
              <th>Сообщения</th>
            </tr>
          </thead>
          {filtered.length === 0 && collapsedHeaders.length === 0 ? (
            <tbody>
              <tr>
                <td colSpan={3} style={{ textAlign: 'center', padding: 24 }}>
                  <Typography.Text view="primary-small" color="secondary" tag="span">
                    Нет строк для текущего фильтра
                  </Typography.Text>
                </td>
              </tr>
            </tbody>
          ) : (
            <>
              {groups.map((group, gi) => {
                const sheetKey = group.sheet ?? '';
                const total = totalsBySheet.get(sheetKey) ?? group.rows.length;
                return (
                  <tbody key={group.sheet ?? `__nosheet__${gi}`}>
                    {showSheetHeaders && (
                      <SheetHeaderRow
                        sheet={group.sheet}
                        total={total}
                        collapsed={false}
                        canToggle={canCollapseAny && !!group.sheet}
                        onToggle={() => group.sheet && toggleSheet(group.sheet)}
                      />
                    )}
                    {group.rows.map((row) => (
                      <tr
                        key={`${row.sheet ?? ''}::${row.rowNumber}`}
                        className={rowClassByStatus(row.status)}
                      >
                        <td>{row.rowNumber}</td>
                        <td>
                          <Status color={ROW_STATUS_COLOR[row.status]} view="soft">
                            {ROW_STATUS_LABEL[row.status]}
                          </Status>
                        </td>
                        <td>
                          {row.errors.length > 0 && (
                            <div className="messages messages--error" style={{ marginTop: 0, marginBottom: row.actions.length > 0 ? 8 : 0 }}>
                              {row.errors.map((e, i) => (
                                <div className="message-row" key={i}>
                                  <span className="message-field">
                                    {e.columnName ?? e.errorCode}
                                  </span>
                                  <span>{e.message}</span>
                                </div>
                              ))}
                            </div>
                          )}
                          {row.actions.length > 0 && (
                            <div className="messages messages--success" style={{ marginTop: 0 }}>
                              {row.actions.map((a, i) => (
                                <div className="message-row" key={i}>
                                  <span className="message-field">Действие</span>
                                  <span>{a}</span>
                                </div>
                              ))}
                            </div>
                          )}
                          {row.errors.length === 0 && row.actions.length === 0 && (
                            <Typography.Text view="primary-small" color="secondary" tag="span">
                              —
                            </Typography.Text>
                          )}
                        </td>
                      </tr>
                    ))}
                  </tbody>
                );
              })}
              {/* Заголовки свёрнутых листов — рисуем под видимыми группами, чтобы
                  пользователь видел, что лист исключён, и мог его развернуть. */}
              {collapsedHeaders.length > 0 && (
                <tbody>
                  {collapsedHeaders.map((sheet) => (
                    <SheetHeaderRow
                      key={`__collapsed__${sheet}`}
                      sheet={sheet}
                      total={totalsBySheet.get(sheet) ?? 0}
                      collapsed={true}
                      canToggle={true}
                      onToggle={() => toggleSheet(sheet)}
                    />
                  ))}
                </tbody>
              )}
            </>
          )}
        </table>
      </div>

      {report.rowsPagination.total > 0 && (() => {
        const { skip, take, total } = report.rowsPagination;
        const pagesCount = Math.max(1, Math.ceil(total / take));
        const currentPageIndex = Math.min(pagesCount - 1, Math.floor(skip / take));
        const rangeFrom = total === 0 ? 0 : skip + 1;
        const rangeTo = Math.min(total, skip + report.rows.length);

        return (
          <div
            style={{
              marginTop: 12,
              display: 'flex',
              alignItems: 'center',
              gap: 16,
              flexWrap: 'wrap',
              justifyContent: 'space-between',
            }}
          >
            <Typography.Text view="primary-small" color="secondary" tag="span">
              Показано {rangeFrom}–{rangeTo} из {total} строк
              {collapsedSheets.size > 0 ? ' (с учётом свёрнутых листов)' : ''}.
            </Typography.Text>
            {pagesCount > 1 && (
              <Pagination
                currentPageIndex={currentPageIndex}
                pagesCount={pagesCount}
                onPageChange={(pageIndex) => {
                  // Сбрасываем фильтр на «Все» при смене страницы, иначе пользователь
                  // может попасть на пустой набор (страница есть, но в текущем фильтре —
                  // ни одной строки) и подумать, что переход не сработал.
                  setFilter('all');
                  onPageChange?.(pageIndex * take, {
                    excludeSheets: Array.from(collapsedSheets).sort(),
                  });
                }}
              />
            )}
          </div>
        );
      })()}
    </div>
  );
};

function rowClassByStatus(s: RowStatus): string {
  if (s === 'Invalid' || s === 'Failed') return 'row--error';
  if (s === 'Pending') return '';
  return '';
}

interface SheetHeaderRowProps {
  sheet: string | null;
  total: number;
  collapsed: boolean;
  canToggle: boolean;
  onToggle: () => void;
}

const SheetHeaderRow = ({
  sheet,
  total,
  collapsed,
  canToggle,
  onToggle,
}: SheetHeaderRowProps) => (
  <tr className={`sheet-header-row${collapsed ? ' sheet-header-row--collapsed' : ''}`}>
    <td colSpan={3}>
      {canToggle ? (
        <button
          type="button"
          className="sheet-header-row__toggle"
          onClick={onToggle}
          aria-expanded={!collapsed}
          aria-label={collapsed ? 'Развернуть лист' : 'Свернуть лист'}
        >
          <span className="sheet-header-row__chevron" aria-hidden="true">
            {collapsed ? '▸' : '▾'}
          </span>
          <span className="sheet-header-row__title">
            Лист: {sheet || '— без листа —'}
          </span>
          <span className="sheet-header-row__count">{total} стр.</span>
          {collapsed && (
            <span className="sheet-header-row__hint">— свёрнут</span>
          )}
        </button>
      ) : (
        <>
          <span className="sheet-header-row__title">
            Лист: {sheet || '— без листа —'}
          </span>
          <span className="sheet-header-row__count">{total} стр.</span>
        </>
      )}
    </td>
  </tr>
);

interface FilterButtonProps {
  active: boolean;
  count: number;
  onClick: () => void;
  children: React.ReactNode;
}

const FilterButton = ({ active, count, onClick, children }: FilterButtonProps) => (
  <button
    type="button"
    className={`filter-tag${active ? ' filter-tag--active' : ''}`}
    onClick={onClick}
  >
    <span>{children}</span>
    <span className="filter-tag__count">{count}</span>
  </button>
);
