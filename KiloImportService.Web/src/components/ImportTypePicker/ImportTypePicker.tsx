import { useMemo } from 'react';
import { Select } from '@alfalab/core-components/select';
import { Typography } from '@alfalab/core-components/typography';
import type { ImportType } from '../../types/import';
import { useImportTypes } from '../../hooks/useImportTypes';

interface Props {
  value: ImportType | null;
  onChange: (value: ImportType | null) => void;
}

export const ImportTypePicker = ({ value, onChange }: Props) => {
  const { data: types, status, error, refetch } = useImportTypes();

  const options = useMemo(
    () =>
      types.map((t) => ({
        key: t.id,
        content: t.isImplemented ? t.label : `${t.label} · скоро`,
        disabled: !t.isImplemented,
      })),
    [types],
  );

  let placeholder = 'Выберите тип импорта';
  if (status === 'loading') placeholder = 'Загрузка типов импорта…';
  else if (status === 'error') placeholder = 'Ошибка загрузки типов';
  else if (types.length === 0) placeholder = 'Типы импорта не найдены';

  return (
    <div className="field">
      <Select
        label="Тип импорта"
        placeholder={placeholder}
        options={options}
        selected={value ?? null}
        onChange={({ selected }) => onChange(selected ? (selected.key as ImportType) : null)}
        disabled={status === 'loading'}
        block
      />
      {status === 'error' && error && (
        <div style={{ display: 'flex', alignItems: 'center', gap: 8, marginTop: 6 }}>
          <Typography.Text view="primary-small" color="negative" tag="span">
            {error}
          </Typography.Text>
          <button
            type="button"
            onClick={refetch}
            style={{
              background: 'transparent',
              border: 'none',
              color: '#ef3124',
              textDecoration: 'underline',
              cursor: 'pointer',
              padding: 0,
              font: 'inherit',
            }}
          >
            Повторить
          </button>
        </div>
      )}
    </div>
  );
};
