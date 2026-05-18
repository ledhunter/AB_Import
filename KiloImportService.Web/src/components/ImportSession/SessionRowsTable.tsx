import { useMemo, useState } from 'react';
import { Status } from '@alfalab/core-components/status';
import { Typography } from '@alfalab/core-components/typography';
import { Pagination } from '@alfalab/core-components/pagination';
import type { RowStatus, UiReport, UiReportRow } from '../../types/session';

interface Props {
  report: UiReport;
  /**
   * Колбэк пагинации: вызывается при клике пользователя на номер страницы
   * (0-based — как в Alfa Pagination). Если не передан — пагинация
   * отображается, но в read-only режиме (без кликов).
   */
  onPageChange?: (skip: number) => void;
}

type Filter = 'all' | 'invalid' | 'valid' | 'applied' | 'failed';

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

function matchesFilter(row: UiReportRow, filter: Filter): boolean {
  if (filter === 'all') return true;
  if (filter === 'valid') return row.status === 'Valid' || row.status === 'Applied';
  if (filter === 'invalid') return row.status === 'Invalid';
  if (filter === 'applied') return row.status === 'Applied';
  if (filter === 'failed') return row.status === 'Failed';
  return false;
}

export const SessionRowsTable = ({ report, onPageChange }: Props) => {
  const [filter, setFilter] = useState<Filter>('all');

  const counts = useMemo(() => {
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
    };
  }, [report.rows]);

  const filtered = useMemo(
    () => report.rows.filter((r) => matchesFilter(r, filter)),
    [report.rows, filter],
  );

  /**
   * Группируем строки по листу для многолистовых импортов.
   * Если sheet у всех строк null/пустой — выводим одну плоскую группу без заголовка.
   * Порядок групп = порядок появления (rows уже отсортированы по (sheet, rowNumber)).
   */
  const grouped = useMemo(() => {
    const groups: { sheet: string | null; rows: UiReportRow[] }[] = [];
    const indexBySheet = new Map<string, number>();
    for (const r of filtered) {
      const key = r.sheet ?? '';
      const idx = indexBySheet.get(key);
      if (idx === undefined) {
        indexBySheet.set(key, groups.length);
        groups.push({ sheet: r.sheet, rows: [r] });
      } else {
        groups[idx].rows.push(r);
      }
    }
    return groups;
  }, [filtered]);

  // Показывать ли заголовки листов: если есть хотя бы один непустой sheet
  // или строки разнесены по нескольким группам.
  const showSheetHeaders = grouped.some((g) => g.sheet && g.sheet.length > 0) || grouped.length > 1;

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
          {filtered.length === 0 ? (
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
            grouped.map((group, gi) => (
              <tbody key={group.sheet ?? `__nosheet__${gi}`}>
                {showSheetHeaders && (
                  <tr className="sheet-header-row">
                    <td colSpan={3}>
                      <span className="sheet-header-row__title">
                        Лист: {group.sheet || '— без листа —'}
                      </span>
                      <span className="sheet-header-row__count">
                        {group.rows.length} стр.
                      </span>
                    </td>
                  </tr>
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
            ))
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
              Показано {rangeFrom}–{rangeTo} из {total} строк.
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
                  onPageChange?.(pageIndex * take);
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
