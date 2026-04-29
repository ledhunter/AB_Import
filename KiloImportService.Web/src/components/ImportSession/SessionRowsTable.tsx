import { useMemo, useState } from 'react';
import { Status } from '@alfalab/core-components/status';
import { Typography } from '@alfalab/core-components/typography';
import type { RowStatus, UiReport, UiReportRow } from '../../types/session';

interface Props {
  report: UiReport;
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

export const SessionRowsTable = ({ report }: Props) => {
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
          <tbody>
            {filtered.length === 0 ? (
              <tr>
                <td colSpan={3} style={{ textAlign: 'center', padding: 24 }}>
                  <Typography.Text view="primary-small" color="secondary" tag="span">
                    Нет строк для текущего фильтра
                  </Typography.Text>
                </td>
              </tr>
            ) : (
              filtered.map((row) => (
                <tr key={row.rowNumber} className={rowClassByStatus(row.status)}>
                  <td>{row.rowNumber}</td>
                  <td>
                    <Status color={ROW_STATUS_COLOR[row.status]} view="soft">
                      {ROW_STATUS_LABEL[row.status]}
                    </Status>
                  </td>
                  <td>
                    {row.errors.length === 0 ? (
                      <Typography.Text view="primary-small" color="secondary" tag="span">
                        —
                      </Typography.Text>
                    ) : (
                      <div className="messages messages--error" style={{ marginTop: 0 }}>
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
                  </td>
                </tr>
              ))
            )}
          </tbody>
        </table>
      </div>

      {report.rowsPagination.total > report.rows.length && (
        <Typography.Text view="primary-small" color="secondary" tag="div" style={{ marginTop: 8 }}>
          Показано {report.rows.length} из {report.rowsPagination.total} строк.
        </Typography.Text>
      )}
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
