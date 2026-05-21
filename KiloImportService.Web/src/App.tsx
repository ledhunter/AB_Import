import { useMemo, useState } from 'react';
import { Button } from '@alfalab/core-components/button';
import { Typography } from '@alfalab/core-components/typography';
import { Alert } from '@alfalab/core-components/alert';
import { ImportTypePicker } from './components/ImportTypePicker/ImportTypePicker';
import { ImportForm } from './components/ImportForm/ImportForm';
import { FileUpload } from './components/FileUpload/FileUpload';
import { SessionView } from './components/ImportSession/SessionView';
import { ImportHistoryPage } from './components/ImportHistory/ImportHistoryPage';
import { useImportSession } from './hooks/useImportSession';
import { useImportTypes } from './hooks/useImportTypes';
import { detectFileFormat } from './utils/fileFormat';
import type { ImportType } from './types/import';
import './App.css';

type AppView = 'import' | 'history';

export default function App() {
  const [view, setView] = useState<AppView>('import');
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

  // Для импорта «Помещения» Site не выбирается в UI — резолвится per-row внутри
  // проекта по (НПС, Этап). См. doc_project/101-rooms-multi-site-by-project.md.
  const requiresSite = importType !== 'rooms';

  const canSubmit =
    importType !== null &&
    projectId !== null &&
    (!requiresSite || siteId !== null) &&
    file !== null &&
    detectedFormat !== null &&
    importSession.phase === 'idle';

  const handleSubmit = async () => {
    if (!file || !importType || projectId === null) return;
    if (requiresSite && siteId === null) return;
    await importSession.start({
      importTypeCode: importType,
      file,
      projectId,
      siteId: requiresSite ? siteId : null,
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
          <nav className="app-nav">
            <button
              type="button"
              className={`app-nav__tab${view === 'import' ? ' app-nav__tab--active' : ''}`}
              onClick={() => setView('import')}
            >
              Импорт
            </button>
            <button
              type="button"
              className={`app-nav__tab${view === 'history' ? ' app-nav__tab--active' : ''}`}
              onClick={() => setView('history')}
            >
              История импортов
            </button>
          </nav>
        </div>
      </header>

      <main className="container app-main">
        {view === 'history' && <ImportHistoryPage />}

        {view === 'import' && isFormPhase && (
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
              showSiteSelect={requiresSite}
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

        {view === 'import' && !isFormPhase && (
          <SessionView
            phase={importSession.phase}
            session={importSession.session}
            report={importSession.report}
            importTypeLabel={importTypeLabel}
            onApply={importSession.apply}
            onCancel={importSession.cancel}
            onReset={handleReset}
            onReportPageChange={importSession.loadReportPage}
          />
        )}
      </main>
    </div>
  );
}
