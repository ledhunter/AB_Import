import { useMemo } from 'react';
import { Select } from '@alfalab/core-components/select';
import type { ImportType } from '../../types/import';
import { IMPORT_TYPES } from '../../mocks/importTypes';

interface Props {
  value: ImportType | null;
  onChange: (value: ImportType | null) => void;
}

export const ImportTypePicker = ({ value, onChange }: Props) => {
  const options = useMemo(
    () =>
      IMPORT_TYPES.map((t) => ({
        key: t.id,
        content: t.label,
      })),
    [],
  );

  return (
    <div className="field">
      <Select
        label="Тип импорта"
        placeholder="Выберите тип импорта"
        options={options}
        selected={value ?? null}
        onChange={({ selected }) => onChange(selected ? (selected.key as ImportType) : null)}
        block
      />
    </div>
  );
};
