import { useMemo, useState } from 'react';
import { Status } from '@alfalab/core-components/status';
import { Typography } from '@alfalab/core-components/typography';
import { Collapse } from '@alfalab/core-components/collapse';
import type { ReportRow, RowStatus } from '../../types/import';

interface Props {
  rows: ReportRow[];
}

type Filter = 'all' | 'success' | 'warning' | 'error';

const STATUS_MAP: Record<RowStatus, { label: string; color: 'green' | 'orange' | 'red' }> = {
  success: { label: 'Успех', color: 'green' },
  warning: { label: 'Предупр.', color: 'orange' },
  error: { label: 'Ошибка', color: 'red' },
};

export const ReportTable = ({ rows }: Props) => {
  const [filter, setFilter] = useState<Filter>('all');
  const [expanded, setExpanded] = useState<number | null>(null);

  const filtered = useMemo(() => {
    if (filter === 'all') return rows;
    return rows.filter((r) => r.status === filter);
  }, [rows, filter]);

  const filters: { id: Filter; label: string; count: number }[] = [
    { id: 'all', label: 'Все', count: rows.length },
    { id: 'success', label: 'Успешные', count: rows.filter((r) => r.status === 'success').length },
    { id: 'warning', label: 'С предупреждениями', count: rows.filter((r) => r.status === 'warning').length },
    { id: 'error', label: 'С ошибками', count: rows.filter((r) => r.status === 'error').length },
  ];

  return (
    <div className="report-table">
      <Typography.Title view="xsmall" tag="h3" weight="bold" style={{ margin: '0 0 16px' }}>
        Построчный отчёт
      </Typography.Title>

      <div className="filter-tags">
        {filters.map((f) => (
          <button
            key={f.id}
            type="button"
            className={`filter-tag ${filter === f.id ? 'filter-tag--active' : ''}`}
            onClick={() => setFilter(f.id)}
          >
            {f.label} <span className="filter-tag__count">{f.count}</span>
          </button>
        ))}
      </div>

      <div className="table-wrapper">
        <table className="data-table">
          <thead>
            <tr>
              <th style={{ width: 60 }}>№</th>
              <th style={{ width: 180 }}>Лист</th>
              <th style={{ width: 110 }}>Статус</th>
              <th>Исходные данные</th>
              <th>Назначение</th>
              <th style={{ width: 110 }}></th>
            </tr>
          </thead>
          <tbody>
            {filtered.map((row) => {
              const isOpen = expanded === row.rowNumber;
              const statusInfo = STATUS_MAP[row.status];
              return (
                <>
                  <tr key={`r-${row.rowNumber}`} className={`row row--${row.status}`}>
                    <td>{row.rowNumber}</td>
                    <td>{row.sheet}</td>
                    <td>
                      <Status color={statusInfo.color} view="soft">
                        {statusInfo.label}
                      </Status>
                    </td>
                    <td className="cell-source">
                      {Object.entries(row.sourceData)
                        .filter(([, v]) => v)
                        .slice(0, 3)
                        .map(([k, v]) => (
                          <span key={k} className="source-pill">
                            <b>{k}:</b> {v}
                          </span>
                        ))}
                    </td>
                    <td className="cell-dest">
                      {row.destinations.length === 0 ? (
                        <Typography.Text view="primary-small" color="secondary">
                          —
                        </Typography.Text>
                      ) : (
                        row.destinations.map((d, i) => (
                          <div key={i} className={`dest-line dest-line--${d.action.toLowerCase()}`}>
                            <span className="dest-action">{d.action}</span>
                            <span className="dest-entity">{d.entity}</span>
                            <span className="dest-title">{d.entityTitle}</span>
                          </div>
                        ))
                      )}
                    </td>
                    <td>
                      <button
                        type="button"
                        className="btn-toggle"
                        onClick={() => setExpanded(isOpen ? null : row.rowNumber)}
                      >
                        {isOpen ? 'Свернуть' : 'Подробнее'}
                      </button>
                    </td>
                  </tr>
                  <tr key={`d-${row.rowNumber}`} className="row-details">
                    <td colSpan={6} style={{ padding: 0, borderBottom: 'none' }}>
                      <Collapse expanded={isOpen}>
                        <div className="row-details__content">
                          <div className="details-grid">
                            <div>
                              <Typography.Text view="primary-small" weight="bold" tag="div">
                                Исходная строка
                              </Typography.Text>
                              <table className="kv-table">
                                <tbody>
                                  {Object.entries(row.sourceData).map(([k, v]) => (
                                    <tr key={k}>
                                      <td>{k}</td>
                                      <td>{v || <span className="muted">—</span>}</td>
                                    </tr>
                                  ))}
                                </tbody>
                              </table>
                            </div>
                            <div>
                              <Typography.Text view="primary-small" weight="bold" tag="div">
                                Загружено в Visary
                              </Typography.Text>
                              {row.destinations.length === 0 ? (
                                <Typography.Text view="primary-small" color="secondary">
                                  Ничего не загружено из-за ошибок
                                </Typography.Text>
                              ) : (
                                row.destinations.map((d, i) => (
                                  <div key={i} className="dest-card">
                                    <div className="dest-card__head">
                                      <Status
                                        color={d.action === 'Created' ? 'green' : d.action === 'Updated' ? 'blue' : 'grey'}
                                        view="soft"
                                      >
                                        {d.action}
                                      </Status>
                                      <b>{d.entity}</b>
                                      <span className="muted">#{d.entityId}</span>
                                    </div>
                                    <div>{d.entityTitle}</div>
                                    <div className="dest-card__path">{d.targetField}</div>
                                  </div>
                                ))
                              )}
                            </div>
                          </div>
                          {row.warnings.length > 0 && (
                            <div className="messages messages--warning">
                              <Typography.Text view="primary-small" weight="bold" tag="div">
                                Предупреждения
                              </Typography.Text>
                              {row.warnings.map((w, i) => (
                                <div key={i} className="message-row">
                                  <span className="message-field">{w.field}</span>
                                  <span>{w.message}</span>
                                </div>
                              ))}
                            </div>
                          )}
                          {row.errors.length > 0 && (
                            <div className="messages messages--error">
                              <Typography.Text view="primary-small" weight="bold" tag="div">
                                Ошибки
                              </Typography.Text>
                              {row.errors.map((e, i) => (
                                <div key={i} className="message-row">
                                  <span className="message-field">{e.field}</span>
                                  <span>{e.message}</span>
                                </div>
                              ))}
                            </div>
                          )}
                        </div>
                      </Collapse>
                    </td>
                  </tr>
                </>
              );
            })}
          </tbody>
        </table>
      </div>
    </div>
  );
};
