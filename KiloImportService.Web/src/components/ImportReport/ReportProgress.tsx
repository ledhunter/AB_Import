import { ProgressBar } from '@alfalab/core-components/progress-bar';
import { Typography } from '@alfalab/core-components/typography';
import type { ImportProgress } from '../../types/import';

interface Props {
  progress: ImportProgress;
}

export const ReportProgress = ({ progress }: Props) => {
  return (
    <div className="report-progress">
      <div className="report-progress__header">
        <Typography.Text view="primary-medium" weight="bold" tag="div">
          Идёт обработка файла…
        </Typography.Text>
        <Typography.Text view="primary-medium" color="secondary" tag="div">
          {progress.currentRow} из {progress.totalRows} строк ({progress.percentComplete}%)
        </Typography.Text>
      </div>
      <ProgressBar value={progress.percentComplete} view="accent" />
      <Typography.Text view="primary-small" color="secondary" tag="div" style={{ marginTop: 8 }}>
        Текущий лист: <b>{progress.currentSheet}</b>
      </Typography.Text>
    </div>
  );
};
