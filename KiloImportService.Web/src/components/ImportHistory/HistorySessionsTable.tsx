import { Checkbox } from '@alfalab/core-components/checkbox';
import { Typography } from '@alfalab/core-components/typography';
import { SessionStatusBadge } from '../ImportSession/SessionStatusBadge';
import { SESSION_STATUS_LABELS } from '../ImportSession/labels';
import { formatDateTime } from '../../utils/datetime';
import type { UiSessionSummary } from '../../types/session';
import type { ApiImportTypeInfo } from '../../types/api';

interface Props {
  items: UiSessionSummary[];
  loading: boolean;
  error: string | null;
  importTypes: ApiImportTypeInfo[];
  selectedIds: Set<string>;
  onOpen: (sessionId: string) => void;
  onToggleOne: (sessionId: string) => void;
  onToggleAllOnPage: (select: boolean) => void;
}

const importTypeLabel = (code: string, types: ApiImportTypeInfo[]): string =>
  types.find((t) => t.id === code)?.label ?? code;

export const HistorySessionsTable = ({
  items,
  loading,
  error,
  importTypes,
  selectedIds,
  onOpen,
  onToggleOne,
  onToggleAllOnPage,
}: Props) => {
  const selectedOnPage = items.filter((s) => selectedIds.has(s.sessionId)).length;
  const allOnPageSelected = items.length > 0 && selectedOnPage === items.length;
  const someOnPageSelected = selectedOnPage > 0 && selectedOnPage < items.length;
  if (error) {
    return (
      <div className="messages messages--error">
        <Typography.Text view="primary-medium" weight="bold" tag="div">
          Не удалось загрузить историю импортов
        </Typography.Text>
        <Typography.Text view="primary-small" tag="div" style={{ marginTop: 4 }}>
          {error}
        </Typography.Text>
      </div>
    );
  }

  if (!loading && items.length === 0) {
    return (
      <div style={{ padding: 32, textAlign: 'center' }}>
        <Typography.Text view="primary-medium" color="secondary" tag="div">
          Сессий импорта пока нет. Запустите первый импорт на вкладке «Импорт».
        </Typography.Text>
      </div>
    );
  }

  return (
    <div className="table-wrapper">
      <table className="data-table">
        <thead>
          <tr>
            <th style={{ width: 44 }}>
              <Checkbox
                checked={allOnPageSelected}
                indeterminate={someOnPageSelected}
                onChange={(_, payload) => onToggleAllOnPage(Boolean(payload?.checked))}
              />
            </th>
            <th style={{ width: 170 }}>Начало</th>
            <th>Файл</th>
            <th style={{ width: 160 }}>Тип</th>
            <th style={{ width: 170 }}>Статус</th>
            <th style={{ width: 100 }}>Всего</th>
            <th style={{ width: 100 }}>OK</th>
            <th style={{ width: 100 }}>Ошибок</th>
            <th style={{ width: 130 }}>Длительность</th>
          </tr>
        </thead>
        <tbody>
          {loading && items.length === 0 ? (
            <tr>
              <td colSpan={9} style={{ textAlign: 'center', padding: 24 }}>
                <Typography.Text view="primary-small" color="secondary" tag="span">
                  Загрузка истории…
                </Typography.Text>
              </td>
            </tr>
          ) : (
            items.map((s) => (
              <tr
                key={s.sessionId}
                className={`row history-row${s.variant === 'failed' ? ' row--error' : ''}${
                  selectedIds.has(s.sessionId) ? ' row--selected' : ''
                }`}
                style={{ cursor: 'pointer' }}
                onClick={() => onOpen(s.sessionId)}
              >
                <td onClick={(e) => e.stopPropagation()}>
                  <Checkbox
                    checked={selectedIds.has(s.sessionId)}
                    onChange={() => onToggleOne(s.sessionId)}
                  />
                </td>
                <td>{formatDateTime(s.startedAt)}</td>
                <td>
                  <div style={{ display: 'flex', flexDirection: 'column', gap: 2 }}>
                    <Typography.Text view="primary-medium" weight="medium" tag="span">
                      {s.fileName}
                    </Typography.Text>
                    {s.fileFormat ? (
                      <Typography.Text view="primary-small" color="secondary" tag="span">
                        {s.fileFormat.toUpperCase()}
                      </Typography.Text>
                    ) : null}
                  </div>
                </td>
                <td>
                  <Typography.Text view="primary-small" tag="span">
                    {importTypeLabel(s.importTypeCode, importTypes)}
                  </Typography.Text>
                </td>
                <td>
                  <SessionStatusBadge
                    variant={s.variant}
                    label={SESSION_STATUS_LABELS[s.status]}
                  />
                </td>
                <td>{s.totalRows}</td>
                <td>
                  <Typography.Text view="primary-small" color="positive" tag="span">
                    {s.successRows}
                  </Typography.Text>
                </td>
                <td>
                  <Typography.Text
                    view="primary-small"
                    color={s.errorRows > 0 ? 'negative' : 'secondary'}
                    tag="span"
                  >
                    {s.errorRows}
                  </Typography.Text>
                </td>
                <td>{s.duration ?? '—'}</td>
              </tr>
            ))
          )}
        </tbody>
      </table>
    </div>
  );
};
