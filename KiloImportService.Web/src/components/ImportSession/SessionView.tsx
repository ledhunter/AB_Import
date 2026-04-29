import { Divider } from '@alfalab/core-components/divider';
import { Button } from '@alfalab/core-components/button';
import { Typography } from '@alfalab/core-components/typography';
import { SessionProgress } from './SessionProgress';
import { SessionSummary } from './SessionSummary';
import { SessionRowsTable } from './SessionRowsTable';
import type { ImportPhase } from '../../hooks/useImportSession';
import type { UiReport, UiSession } from '../../types/session';

interface Props {
  phase: ImportPhase;
  session: UiSession | null;
  report: UiReport | null;
  importTypeLabel?: string;
  onApply: () => void;
  onCancel: () => void;
  onReset: () => void;
}

/**
 * Композирует представление активной/завершённой сессии импорта.
 * Логика «когда что показывать» вынесена сюда из App.tsx.
 */
export const SessionView = ({
  phase,
  session,
  report,
  importTypeLabel,
  onApply,
  onCancel,
  onReset,
}: Props) => {
  if (!session) {
    return (
      <div className="card">
        <Typography.Text view="primary-medium" tag="div">
          Загрузка сессии…
        </Typography.Text>
      </div>
    );
  }

  // Прогресс-вью пока сессия не завершилась.
  const isProgressView =
    phase === 'uploading' ||
    phase === 'tracking' ||
    phase === 'applying' ||
    session.variant === 'progress' ||
    session.variant === 'pending';

  return (
    <>
      <div className="card">
        {isProgressView ? (
          <SessionProgress session={session} />
        ) : (
          <>
            <SessionSummary session={session} importTypeLabel={importTypeLabel} />
            {report && (
              <>
                <div className="section-gap">
                  <Divider />
                </div>
                <SessionRowsTable report={report} />
              </>
            )}
          </>
        )}
      </div>

      <div className="form-actions" style={{ marginTop: 24, display: 'flex', gap: 12 }}>
        {session.variant === 'awaiting' && (
          <>
            <Button view="primary" size={56} onClick={onApply} disabled={phase === 'applying'}>
              Применить в Visary
            </Button>
            <Button view="secondary" size={56} onClick={onCancel}>
              Отменить сессию
            </Button>
          </>
        )}
        {(session.variant === 'success' ||
          session.variant === 'failed' ||
          session.variant === 'cancelled') && (
          <Button view="secondary" size={56} onClick={onReset}>
            Новый импорт
          </Button>
        )}
        {(session.variant === 'pending' || session.variant === 'progress') &&
          phase !== 'applying' && (
            <Button view="secondary" size={56} onClick={onCancel}>
              Отменить
            </Button>
          )}
      </div>
    </>
  );
};
