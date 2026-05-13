import { SelectDesktop as Select } from '@alfalab/core-components/select/desktop';
import { Button } from '@alfalab/core-components/button';
import { Typography } from '@alfalab/core-components/typography';
import { SESSION_STATUS_LABELS } from '../ImportSession/labels';
import type { SessionStatus } from '../../types/session';
import type { ApiImportTypeInfo } from '../../types/api';

interface Props {
  status?: string;
  importTypeCode?: string;
  importTypes: ApiImportTypeInfo[];
  total: number;
  loading: boolean;
  onStatusChange: (status: string | undefined) => void;
  onTypeChange: (typeCode: string | undefined) => void;
  onRefresh: () => void;
}

const ALL_OPTION_KEY = '__all__';

const STATUS_KEYS: SessionStatus[] = [
  'Pending',
  'Parsing',
  'Validating',
  'Validated',
  'Applying',
  'Applied',
  'Failed',
  'Cancelled',
];

export const HistoryFilters = ({
  status,
  importTypeCode,
  importTypes,
  total,
  loading,
  onStatusChange,
  onTypeChange,
  onRefresh,
}: Props) => {
  const statusOptions = [
    { key: ALL_OPTION_KEY, content: 'Все статусы' },
    ...STATUS_KEYS.map((s) => ({ key: s, content: SESSION_STATUS_LABELS[s] })),
  ];

  const typeOptions = [
    { key: ALL_OPTION_KEY, content: 'Все типы' },
    ...importTypes.map((t) => ({ key: t.id, content: t.label })),
  ];

  return (
    <div className="history-filters">
      <div className="history-filters__controls">
        <div className="history-filters__field">
          <Select
            label="Статус"
            selected={status ?? ALL_OPTION_KEY}
            options={statusOptions}
            onChange={({ selected }) => {
              const key = selected?.key;
              onStatusChange(!key || key === ALL_OPTION_KEY ? undefined : String(key));
            }}
            block
          />
        </div>
        <div className="history-filters__field">
          <Select
            label="Тип импорта"
            selected={importTypeCode ?? ALL_OPTION_KEY}
            options={typeOptions}
            onChange={({ selected }) => {
              const key = selected?.key;
              onTypeChange(!key || key === ALL_OPTION_KEY ? undefined : String(key));
            }}
            block
          />
        </div>
        <div className="history-filters__action">
          <Button view="secondary" onClick={onRefresh} loading={loading} block>
            Обновить
          </Button>
        </div>
      </div>
      <Typography.Text
        view="primary-small"
        color="secondary"
        tag="div"
        style={{ marginTop: 8 }}
      >
        Найдено сессий: {total}
      </Typography.Text>
    </div>
  );
};
