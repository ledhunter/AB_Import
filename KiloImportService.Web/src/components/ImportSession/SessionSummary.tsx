import { Typography } from '@alfalab/core-components/typography';
import { SessionStatusBadge } from './SessionStatusBadge';
import { SESSION_STATUS_LABELS } from './labels';
import { formatDateTime } from '../../utils/datetime';
import type { UiSession } from '../../types/session';

interface Props {
  session: UiSession;
  importTypeLabel?: string;
}

interface Card {
  label: string;
  value: number | string;
  color: 'positive' | 'link' | 'secondary' | 'attention' | 'negative';
}

export const SessionSummary = ({ session, importTypeLabel }: Props) => {
  const cards: Card[] = [
    { label: 'Всего строк', value: session.totalRows, color: 'secondary' },
    { label: 'Валидных', value: session.successRows, color: 'positive' },
    { label: 'С ошибками', value: session.errorRows, color: 'negative' },
    {
      label: 'Файл',
      value: session.fileFormat ? session.fileFormat.toUpperCase() : '—',
      color: 'link',
    },
  ];

  const subtitleParts = [
    session.fileName,
    session.fileFormat ? session.fileFormat.toUpperCase() : null,
    `Тип: ${importTypeLabel ?? session.importTypeCode}`,
  ].filter(Boolean);

  return (
    <div className="report-summary">
      <div className="report-summary__header">
        <div>
          <Typography.Title view="small" tag="h2" weight="bold" style={{ margin: 0 }}>
            Отчёт импорта
          </Typography.Title>
          <Typography.Text view="primary-small" color="secondary" tag="div" style={{ marginTop: 4 }}>
            {subtitleParts.join(' · ')}
          </Typography.Text>
        </div>
        <SessionStatusBadge
          variant={session.variant}
          label={SESSION_STATUS_LABELS[session.status]}
        />
      </div>

      <div className="report-summary__meta">
        <div className="report-summary__meta-item">
          <Typography.Text view="primary-small" color="secondary" tag="div">
            Начало импорта
          </Typography.Text>
          <Typography.Text view="primary-medium" weight="bold" tag="div" style={{ marginTop: 2 }}>
            {formatDateTime(session.startedAt)}
          </Typography.Text>
        </div>
        <div className="report-summary__meta-item">
          <Typography.Text view="primary-small" color="secondary" tag="div">
            Окончание импорта
          </Typography.Text>
          <Typography.Text view="primary-medium" weight="bold" tag="div" style={{ marginTop: 2 }}>
            {formatDateTime(session.completedAt)}
          </Typography.Text>
        </div>
        <div className="report-summary__meta-item">
          <Typography.Text view="primary-small" color="secondary" tag="div">
            Длительность
          </Typography.Text>
          <Typography.Text view="primary-medium" weight="bold" tag="div" style={{ marginTop: 2 }}>
            {session.duration ?? '—'}
          </Typography.Text>
        </div>
      </div>

      <div className="report-summary__cards">
        {cards.map((c) => (
          <div className="summary-card" key={c.label}>
            <Typography.Text view="primary-small" color="secondary" tag="div">
              {c.label}
            </Typography.Text>
            <Typography.Title view="small" tag="div" color={c.color} weight="bold" style={{ marginTop: 4 }}>
              {c.value}
            </Typography.Title>
          </div>
        ))}
      </div>

      {session.errorMessage && (
        <Typography.Text view="primary-small" color="negative" tag="div" style={{ marginTop: 12 }}>
          {session.errorMessage}
        </Typography.Text>
      )}
    </div>
  );
};
