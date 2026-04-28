import { Divider } from '@alfalab/core-components/divider';
import { ReportSummary } from './ReportSummary';
import { ReportTable } from './ReportTable';
import { ReportProgress } from './ReportProgress';
import type { ImportReport as ImportReportType, ImportProgress } from '../../types/import';

interface Props {
  report: ImportReportType | null;
  progress: ImportProgress | null;
  isProcessing: boolean;
}

export const ImportReport = ({ report, progress, isProcessing }: Props) => {
  if (isProcessing && progress) {
    return (
      <div className="card">
        <ReportProgress progress={progress} />
        {report && (
          <>
            <div className="section-gap"><Divider /></div>
            <ReportSummary report={report} />
            <div className="section-gap"><Divider /></div>
            <ReportTable rows={report.rows} />
          </>
        )}
      </div>
    );
  }

  if (!report) return null;

  return (
    <div className="card">
      <ReportSummary report={report} />
      <div className="section-gap"><Divider /></div>
      <ReportTable rows={report.rows} />
    </div>
  );
};
