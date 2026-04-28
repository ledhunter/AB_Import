import { Typography } from '@alfalab/core-components/typography';
import type { ImportReport } from '../../types/import';
import { IMPORT_TYPES } from '../../mocks/importTypes';
import { formatDateTime } from '../../utils/datetime';

interface Props {
  report: ImportReport;
}

const getImportTypeLabel = (id: string): string =>
  IMPORT_TYPES.find((t) => t.id === id)?.label ?? id;

interface Card {
  label: string;
  value: number | string;
  color: 'positive' | 'link' | 'secondary' | 'attention' | 'negative';
}

export const ReportSummary = ({ report }: Props) => {
  const { summary } = report;

  const cards: Card[] = [
    { label: 'Всего строк', value: summary.totalRows, color: 'secondary' },
    { label: 'Помещений создано', value: summary.roomsCreated, color: 'positive' },
    { label: 'Помещений обновлено', value: summary.roomsUpdated, color: 'link' },
    { label: 'ДДУ создано', value: summary.shareAgreementsCreated, color: 'positive' },
    { label: 'ДДУ обновлено', value: summary.shareAgreementsUpdated, color: 'link' },
    { label: 'Пропущено', value: summary.roomsSkipped + summary.shareAgreementsSkipped, color: 'secondary' },
    { label: 'Предупреждений', value: summary.warningsCount, color: 'attention' },
    { label: 'Ошибок', value: summary.errorsCount, color: 'negative' },
  ];

  return (
    <div className="report-summary">
      <div className="report-summary__header">
        <div>
          <Typography.Title view="small" tag="h2" weight="bold" style={{ margin: 0 }}>
            Отчёт импорта
          </Typography.Title>
          <Typography.Text view="primary-small" color="secondary" tag="div" style={{ marginTop: 4 }}>
            {report.fileName} · {report.fileFormat.toUpperCase()} · Тип: {getImportTypeLabel(report.importType)}
          </Typography.Text>
        </div>
      </div>

      <div className="report-summary__meta">
        <div className="report-summary__meta-item">
          <Typography.Text view="primary-small" color="secondary" tag="div">
            Начало импорта
          </Typography.Text>
          <Typography.Text view="primary-medium" weight="bold" tag="div" style={{ marginTop: 2 }}>
            {formatDateTime(report.startedAt)}
          </Typography.Text>
        </div>
        <div className="report-summary__meta-item">
          <Typography.Text view="primary-small" color="secondary" tag="div">
            Окончание импорта
          </Typography.Text>
          <Typography.Text view="primary-medium" weight="bold" tag="div" style={{ marginTop: 2 }}>
            {formatDateTime(report.completedAt)}
          </Typography.Text>
        </div>
        <div className="report-summary__meta-item">
          <Typography.Text view="primary-small" color="secondary" tag="div">
            Длительность
          </Typography.Text>
          <Typography.Text view="primary-medium" weight="bold" tag="div" style={{ marginTop: 2 }}>
            {report.duration ?? '—'}
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
    </div>
  );
};
