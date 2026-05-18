import { Button } from '@alfalab/core-components/button';
import { Divider } from '@alfalab/core-components/divider';
import { Typography } from '@alfalab/core-components/typography';
import { SessionSummary } from '../ImportSession/SessionSummary';
import { SessionRowsTable } from '../ImportSession/SessionRowsTable';
import { SessionGeneratedFiles } from '../ImportSession/SessionGeneratedFiles';
import { useImportSessionDetail } from '../../hooks/useImportSessionDetail';
import type { ApiImportTypeInfo } from '../../types/api';

interface Props {
  sessionId: string;
  importTypes: ApiImportTypeInfo[];
  onBack: () => void;
}

/**
 * Read-only детальный просмотр сессии из истории.
 * Переиспользует те же `SessionSummary` + `SessionRowsTable`, что и активный
 * импорт — но без apply/cancel и без SignalR-подписки.
 */
export const HistoryDetailView = ({ sessionId, importTypes, onBack }: Props) => {
  const { session, report, loading, error, reload, loadReportPage } = useImportSessionDetail(sessionId);

  const importTypeLabel = session
    ? (importTypes.find((t) => t.id === session.importTypeCode)?.label ?? undefined)
    : undefined;

  return (
    <>
      <div
        style={{
          display: 'flex',
          alignItems: 'center',
          gap: 12,
          marginBottom: 16,
        }}
      >
        <Button view="secondary" size={40} onClick={onBack}>
          ← К списку
        </Button>
        <Button view="text" size={40} onClick={reload} loading={loading}>
          Обновить
        </Button>
      </div>

      <div className="card">
        {error && (
          <div className="messages messages--error" style={{ marginBottom: 16 }}>
            <Typography.Text view="primary-medium" weight="bold" tag="div">
              Ошибка загрузки сессии
            </Typography.Text>
            <Typography.Text view="primary-small" tag="div" style={{ marginTop: 4 }}>
              {error}
            </Typography.Text>
          </div>
        )}

        {!session && loading && (
          <Typography.Text view="primary-medium" tag="div">
            Загрузка сессии…
          </Typography.Text>
        )}

        {session && (
          <>
            <SessionSummary session={session} importTypeLabel={importTypeLabel} />
            {report && (
              <>
                <div className="section-gap">
                  <Divider />
                </div>
                <SessionRowsTable report={report} onPageChange={loadReportPage} />
              </>
            )}
            {!report && session.variant !== 'pending' && session.variant !== 'progress' && (
              <Typography.Text
                view="primary-small"
                color="secondary"
                tag="div"
                style={{ marginTop: 12 }}
              >
                Подробный отчёт по строкам недоступен для этого статуса.
              </Typography.Text>
            )}
            {session.generatedFiles.length > 0 && (
              <>
                <div className="section-gap">
                  <Divider />
                </div>
                <SessionGeneratedFiles files={session.generatedFiles} />
              </>
            )}
          </>
        )}
      </div>
    </>
  );
};
