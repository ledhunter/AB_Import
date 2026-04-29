import { Status } from '@alfalab/core-components/status';
import type { SessionStatusVariant } from '../../types/session';

interface Props {
  variant: SessionStatusVariant;
  label: string;
}

const colorByVariant: Record<SessionStatusVariant, 'green' | 'red' | 'orange' | 'blue' | 'grey'> = {
  pending: 'grey',
  progress: 'blue',
  awaiting: 'orange',
  success: 'green',
  failed: 'red',
  cancelled: 'grey',
};

/** Цветной бейдж статуса сессии импорта. */
export const SessionStatusBadge = ({ variant, label }: Props) => (
  <Status color={colorByVariant[variant]} view="soft">
    {label}
  </Status>
);
