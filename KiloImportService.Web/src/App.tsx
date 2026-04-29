import { useMemo, useState } from 'react';
import { Button } from '@alfalab/core-components/button';
import { Typography } from '@alfalab/core-components/typography';
import { Alert } from '@alfalab/core-components/alert';
import { ImportTypePicker } from './components/ImportTypePicker/ImportTypePicker';
import { ImportForm } from './components/ImportForm/ImportForm';
import { FileUpload } from './components/FileUpload/FileUpload';
import { SessionView } from './components/ImportSession/SessionView';
import { useImportSession } from './hooks/useImportSession';
import { useImportTypes } from './hooks/useImportTypes';
import { detectFileFormat } from './utils/fileFormat';
import type { ImportType } from './types/import';
import './App.css';

export default function App() {
  const [importType, setImportType] = useState<ImportType | null>(null);
  const [projectId, setProjectId] = useState<number | null>(null);
  const [siteId, setSiteId] = useState<number | null>(null);
  const [file, setFile] = useState<File | null>(null);

  const importSession = useImportSession();
  const importTypes = useImportTypes();

  const detectedFormat = useMemo(() => (file ? detectFileFormat(file.name) : null), [file]);
  const importTypeLabel = useMemo(
    () => importTypes.data.find((t) => t.id === importType)?.label ?? importType ?? undefined,
    [importTypes.data, importType],
  );

  const canSubmit =
    importType !== null &&
    projectId !== null &&
    siteId !== null &&
    file !== null &&
    detectedFormat !== null &&
    importSession.phase === 'idle';

  const handleSubmit = async () => {
    if (!file || !importType || projectId === null || siteId === null) return;
    await importSession.start({
      importTypeCode: importType,
      file,
      projectId,
      siteId,
    });
  };

  const handleReset = () => {
    importSession.reset();
    setFile(null);
  };

  const isFormPhase = importSession.phase === 'idle';

  return (
    <div className="app">
      <header className="app-header">
        <div className="container">
          <Typography.Title view="medium" tag="h1" weight="bold" style={{ margin: 0 }}>
            Сервис импорта файлов
          </Typography.Title>
          <Typography.Text
            view="primary-medium"
            color="secondary"
            tag="div"
            style={{ marginTop: 4 }}
          >
            Visary · Альфа Банк — Управление проектами
          </Typography.Text>
        </div>
      </header>

      <main className="container app-main">
        {isFormPhase && (
          <div className="card">
            <Typography.Title
              view="small"
              tag="h2"
              weight="bold"
              style={{ margin: '0 0 24px' }}
            >
              Параметры импорта
            </Typography.Title>

            <ImportTypePicker value={importType} onChange={setImportType} />

            <ImportForm
              projectId={projectId}
              siteId={siteId}
              onProjectChange={setProjectId}
              onSiteChange={setSiteId}
            />

            <FileUpload
              file={file}
              detectedFormat={detectedFormat}
              onFileSelect={setFile}
            />

            {importSession.error && (
              <div style={{ marginBottom: 16 }}>
                <Alert view="negative">{importSession.error}</Alert>
              </div>
            )}

            <div className="form-actions">
              <Button
                view="primary"
                size={56}
                onClick={handleSubmit}
                disabled={!canSubmit}
                loading={importSession.phase === 'uploading'}
                block
              >
                Запустить импорт
              </Button>
            </div>
          </div>
        )}

        {!isFormPhase && (
          <SessionView
            phase={importSession.phase}
            session={importSession.session}
            report={importSession.report}
            importTypeLabel={importTypeLabel}
            onApply={importSession.apply}
            onCancel={importSession.cancel}
            onReset={handleReset}
          />
        )}
      </main>
    </div>
  );
}
