import { Button } from '@alfalab/core-components/button';
import { Typography } from '@alfalab/core-components/typography';

interface Props {
  skip: number;
  take: number;
  total: number;
  onChange: (nextSkip: number) => void;
}

export const HistoryPagination = ({ skip, take, total, onChange }: Props) => {
  if (total <= take) return null;

  const from = Math.min(skip + 1, total);
  const to = Math.min(skip + take, total);
  const canPrev = skip > 0;
  const canNext = skip + take < total;

  return (
    <div
      style={{
        display: 'flex',
        justifyContent: 'space-between',
        alignItems: 'center',
        marginTop: 16,
        gap: 12,
      }}
    >
      <Typography.Text view="primary-small" color="secondary" tag="span">
        {from}–{to} из {total}
      </Typography.Text>
      <div style={{ display: 'flex', gap: 8 }}>
        <Button
          view="secondary"
          size={40}
          disabled={!canPrev}
          onClick={() => onChange(Math.max(0, skip - take))}
        >
          Назад
        </Button>
        <Button
          view="secondary"
          size={40}
          disabled={!canNext}
          onClick={() => onChange(skip + take)}
        >
          Вперёд
        </Button>
      </div>
    </div>
  );
};
