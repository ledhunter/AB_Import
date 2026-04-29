import { ProgressBar } from '@alfalab/core-components/progress-bar';
import { Typography } from '@alfalab/core-components/typography';
import { SessionStatusBadge } from './SessionStatusBadge';
import { SESSION_STATUS_LABELS, STAGE_LABELS } from './labels';
import type { UiSession } from '../../types/session';

interface Props {
  session: UiSession;
}

/**
 * Подсчитывает агрегированный процент прогресса по стадиям:
 *   Parse: 0..40%, Validate: 40..80%, Apply: 80..100%.
 * Если есть live-прогресс по строкам (`stageProgress`) — он усиливает значение
 * внутри текущей стадии. Если сессия в финальном статусе — 100%.
 */
function computePercent(session: UiSession): number {
  if (session.variant === 'success') return 100;
  if (session.variant === 'failed' || session.variant === 'cancelled') return 100;

  const ranges: Record<'Upload' | 'Parse' | 'Validate' | 'Apply', [number, number]> = {
    Upload: [0, 5],
    Parse: [5, 40],
    Validate: [40, 80],
    Apply: [80, 100],
  };

  let percent = 0;
  for (const stage of session.stages) {
    const [from, to] = ranges[stage.kind];
    const stagePercent = stage.completedAt ? 100 : Math.max(0, stage.progressPercent);
    percent = Math.max(percent, from + ((to - from) * stagePercent) / 100);
  }
  // Live-прогресс по строкам перекрывает грубый stage.progressPercent (обычно 0).
  if (session.stageProgress) {
    const [from, to] = ranges[session.stageProgress.stage];
    percent = Math.max(percent, from + ((to - from) * session.stageProgress.percentComplete) / 100);
  }
  // Если есть Validated/Awaiting (готов к Apply) — считаем что мы на 80%.
  if (session.variant === 'awaiting') percent = Math.max(percent, 80);
  return Math.min(99, Math.round(percent));
}

export const SessionProgress = ({ session }: Props) => {
  const percent = computePercent(session);
  const currentStage = session.stages.find((s) => !s.completedAt);
  const live = session.stageProgress;

  return (
    <div className="session-progress">
      <div className="session-progress__header">
        <div>
          <Typography.Title view="small" tag="h2" weight="bold" style={{ margin: 0 }}>
            Импорт в процессе
          </Typography.Title>
          <Typography.Text view="primary-small" color="secondary" tag="div" style={{ marginTop: 4 }}>
            {session.fileName} · {session.fileFormat.toUpperCase()}
          </Typography.Text>
        </div>
        <SessionStatusBadge
          variant={session.variant}
          label={SESSION_STATUS_LABELS[session.status]}
        />
      </div>

      <div style={{ marginTop: 24 }}>
        <ProgressBar value={percent} view="accent" size={8} />
        <div className="session-progress__meta">
          <Typography.Text view="primary-small" color="secondary" tag="span">
            {currentStage
              ? `Этап: ${STAGE_LABELS[currentStage.kind]}${currentStage.message ? ` — ${currentStage.message}` : ''}`
              : 'Ожидание…'}
          </Typography.Text>
          <Typography.Text view="primary-small" weight="bold" tag="span">
            {percent}%
          </Typography.Text>
        </div>
      </div>

      {live && live.totalRows > 0 && (
        <Typography.Text
          view="primary-small"
          color="secondary"
          tag="div"
          style={{ marginTop: 12 }}
        >
          {STAGE_LABELS[live.stage]}: строка {live.currentRow} из {live.totalRows}
          {live.sheet ? ` (лист «${live.sheet}»)` : ''} · {live.percentComplete}%
        </Typography.Text>
      )}

      {!live && session.totalRows > 0 && (
        <Typography.Text
          view="primary-small"
          color="secondary"
          tag="div"
          style={{ marginTop: 12 }}
        >
          Строк: {session.totalRows} · валидных: {session.successRows} · с ошибками: {session.errorRows}
        </Typography.Text>
      )}
    </div>
  );
};
