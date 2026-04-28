import { useMemo } from 'react';
import { Select } from '@alfalab/core-components/select';
import { Typography } from '@alfalab/core-components/typography';
import { MOCK_SITES } from '../../mocks/data';
import { useProjects } from '../../hooks/useProjects';

interface Props {
  projectId: number | null;
  siteId: number | null;
  onProjectChange: (id: number | null) => void;
  onSiteChange: (id: number | null) => void;
}

export const ImportForm = ({
  projectId,
  siteId,
  onProjectChange,
  onSiteChange,
}: Props) => {
  const { data: projects, status, error, load, refetch } = useProjects();

  const projectOptions = useMemo(
    () => projects.map((p) => ({ key: String(p.id), content: `${p.title} (${p.code})` })),
    [projects],
  );

  // TODO: заменить на useSites(projectId), когда появится endpoint /listview/constructionsite
  const siteOptions = useMemo(() => {
    if (!projectId) return [];
    const sites = MOCK_SITES[projectId] ?? [];
    return sites.map((s) => ({
      key: String(s.id),
      content: `${s.title} · ${s.constructionPermissionNumber} · ${s.stageNumber}`,
    }));
  }, [projectId]);

  // Lazy-load: запрашиваем список проектов только при первом открытии Select.
  const handleProjectsOpen = (payload: { open?: boolean }) => {
    console.info(
      `[ImportForm] Select "Проект" onOpen — open=${payload.open}, status=${status}`,
    );
    if (payload.open) load();
  };

  let projectPlaceholder: string;
  switch (status) {
    case 'loading':
      projectPlaceholder = 'Загрузка проектов…';
      break;
    case 'error':
      projectPlaceholder = 'Ошибка загрузки проектов';
      break;
    case 'success':
      projectPlaceholder = projects.length === 0 ? 'Проекты не найдены' : 'Выберите проект';
      break;
    default:
      projectPlaceholder = 'Нажмите для загрузки проектов';
  }

  return (
    <div className="form-grid">
      <div className="field">
        <Select
          label="Проект"
          placeholder={projectPlaceholder}
          options={projectOptions}
          selected={projectId !== null ? String(projectId) : null}
          onChange={({ selected }) => {
            onProjectChange(selected ? Number(selected.key) : null);
            onSiteChange(null);
          }}
          onOpen={handleProjectsOpen}
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

      <div className="field">
        <Select
          label="Объект строительства"
          placeholder={projectId ? 'Выберите объект' : 'Сначала выберите проект'}
          options={siteOptions}
          selected={siteId !== null ? String(siteId) : null}
          onChange={({ selected }) => onSiteChange(selected ? Number(selected.key) : null)}
          disabled={!projectId}
          block
        />
      </div>
    </div>
  );
};
