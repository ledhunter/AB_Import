import { useMemo, useState } from 'react';
import { Button } from '@alfalab/core-components/button';
import { Typography } from '@alfalab/core-components/typography';
import { ImportTypePicker } from './components/ImportTypePicker/ImportTypePicker';
import { ImportForm } from './components/ImportForm/ImportForm';
import { FileUpload } from './components/FileUpload/FileUpload';
import { ImportReport } from './components/ImportReport/ImportReport';
import { MOCK_PROGRESS, MOCK_REPORT } from './mocks/data';
import { detectFileFormat } from './utils/fileFormat';
import type { ImportType, ImportProgress, ImportReport as ImportReportT } from './types/import';
import './App.css';

type Stage = 'form' | 'processing' | 'completed';

export default function App() {
  const [stage, setStage] = useState<Stage>('form');
  const [importType, setImportType] = useState<ImportType | null>(null);
  const [projectId, setProjectId] = useState<number | null>(null);
  const [siteId, setSiteId] = useState<number | null>(null);
  const [file, setFile] = useState<File | null>(null);
  const [progress, setProgress] = useState<ImportProgress | null>(null);
  const [report, setReport] = useState<ImportReportT | null>(null);

  const detectedFormat = useMemo(() => (file ? detectFileFormat(file.name) : null), [file]);
  const canSubmit =
    importType !== null && projectId !== null && siteId !== null && file !== null && detectedFormat !== null;

  const handleSubmit = () => {
    setStage('processing');
    setProgress({ ...MOCK_PROGRESS, currentRow: 0, percentComplete: 0 });
    setReport(null);

    let cur = 0;
    const total = MOCK_PROGRESS.totalRows;
    const interval = setInterval(() => {
      cur += Math.ceil(total / 25);
      if (cur >= total) {
        clearInterval(interval);
        setProgress({ ...MOCK_PROGRESS, currentRow: total, percentComplete: 100 });
        setReport(MOCK_REPORT);
        setStage('completed');
      } else {
        setProgress({
          ...MOCK_PROGRESS,
          currentRow: cur,
          percentComplete: Math.round((cur / total) * 100),
        });
      }
    }, 200);
  };

  const handleReset = () => {
    setStage('form');
    setFile(null);
    setProgress(null);
    setReport(null);
  };

  return (
    <div className="app">
      <header className="app-header">
        <div className="container">
          <Typography.Title view="medium" tag="h1" weight="bold" style={{ margin: 0 }}>
            Сервис импорта файлов
          </Typography.Title>
          <Typography.Text view="primary-medium" color="secondary" tag="div" style={{ marginTop: 4 }}>
            Visary · Альфа Банк — Управление проектами
          </Typography.Text>
        </div>
      </header>

      <main className="container app-main">
        {stage === 'form' && (
          <div className="card">
            <Typography.Title view="small" tag="h2" weight="bold" style={{ margin: '0 0 24px' }}>
              Параметры импорта
            </Typography.Title>

            <ImportTypePicker value={importType} onChange={setImportType} />

            <ImportForm
              projectId={projectId}
              siteId={siteId}
              onProjectChange={setProjectId}
              onSiteChange={setSiteId}
            />

            <FileUpload file={file} detectedFormat={detectedFormat} onFileSelect={setFile} />

            <div className="form-actions">
              <Button view="primary" size={56} onClick={handleSubmit} disabled={!canSubmit} block>
                Запустить импорт
              </Button>
            </div>
          </div>
        )}

        {(stage === 'processing' || stage === 'completed') && (
          <>
            <ImportReport
              report={report}
              progress={progress}
              isProcessing={stage === 'processing'}
            />
            {stage === 'completed' && (
              <div className="form-actions" style={{ marginTop: 24 }}>
                <Button view="secondary" size={56} onClick={handleReset}>
                  Новый импорт
                </Button>
              </div>
            )}
          </>
        )}
      </main>
    </div>
  );
}
