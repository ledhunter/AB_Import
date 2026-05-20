import { useEffect, useMemo, useState } from 'react';
import { SelectDesktop as Select } from '@alfalab/core-components/select/desktop';
import { Button } from '@alfalab/core-components/button';
import { Typography } from '@alfalab/core-components/typography';
import { SESSION_STATUS_LABELS } from '../ImportSession/labels';
import type { SessionStatus } from '../../types/session';
import type { ApiImportTypeInfo } from '../../types/api';
import { useBackendProjects } from '../../hooks/useBackendProjects';

interface Props {
  status?: string;
  importTypeCode?: string;
  projectId?: number;
  importTypes: ApiImportTypeInfo[];
  total: number;
  loading: boolean;
  onStatusChange: (status: string | undefined) => void;
  onTypeChange: (typeCode: string | undefined) => void;
  onProjectChange: (projectId: number | undefined) => void;
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
  projectId,
  importTypes,
  total,
  loading,
  onStatusChange,
  onTypeChange,
  onProjectChange,
  onRefresh,
}: Props) => {
  // Лёгкий локальный поиск по проектам — фильтр в истории нужен по тем,
  // что уже встречались в сессиях, но Select поверх кэша Visary даёт более
  // богатый опыт (autocomplete) и единый UX с формой импорта.
  const [projectSearch, setProjectSearch] = useState('');
  const projects = useBackendProjects({
    searchString: projectSearch,
    logTag: '[HistoryFilters/Projects]',
  });

  // Прогреваем кэш проектов при первом монтировании страницы, иначе пользователь
  // увидит «Проект» с пустым списком пока не начнёт что-то печатать.
  useEffect(() => {
    projects.sync();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const statusOptions = [
    { key: ALL_OPTION_KEY, content: 'Все статусы' },
    ...STATUS_KEYS.map((s) => ({ key: s, content: SESSION_STATUS_LABELS[s] })),
  ];

  const typeOptions = [
    { key: ALL_OPTION_KEY, content: 'Все типы' },
    ...importTypes.map((t) => ({ key: t.id, content: t.label })),
  ];

  const projectOptions = useMemo(() => {
    const items = projects.data.map((p) => ({
      key: String(p.id),
      content: p.title,
    }));
    // Если выбранный проект (projectId) пропал из кэша — всё равно держим его
    // в списке, иначе Select снимет выбор и пользователь потеряет фильтр.
    if (projectId != null && !items.some((o) => o.key === String(projectId))) {
      items.unshift({ key: String(projectId), content: `Проект #${projectId}` });
    }
    return [{ key: ALL_OPTION_KEY, content: 'Все проекты' }, ...items];
  }, [projects.data, projectId]);

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
        <div className="history-filters__field">
          <Select
            label="Проект"
            selected={projectId != null ? String(projectId) : ALL_OPTION_KEY}
            options={projectOptions}
            showSearch
            searchProps={{
              value: projectSearch,
              // Alfa Select передаёт уже извлечённую строку, а не SyntheticEvent —
              // см. doc_project/20-select-with-search.md и ImportForm.tsx:174.
              // Старый вариант `(e) => e.target.value` падал с
              // «Cannot read properties of undefined (reading 'value')».
              onChange: (value: string) => setProjectSearch(value),
              componentProps: { placeholder: 'Поиск проекта' },
            }}
            onChange={({ selected }) => {
              const key = selected?.key;
              if (!key || key === ALL_OPTION_KEY) {
                onProjectChange(undefined);
              } else {
                const parsed = Number(key);
                onProjectChange(Number.isFinite(parsed) ? parsed : undefined);
              }
              // Очищаем поисковую подстроку — иначе Select после закрытия
              // продолжит фильтровать список по старому запросу.
              setProjectSearch('');
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
