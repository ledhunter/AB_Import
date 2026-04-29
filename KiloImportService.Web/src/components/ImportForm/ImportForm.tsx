import { useMemo } from 'react';
import { Select } from '@alfalab/core-components/select';
import { Typography } from '@alfalab/core-components/typography';
import { useProjects } from '../../hooks/useProjects';
import { useSites } from '../../hooks/useSites';

interface Props {
  projectId: number | null;
  siteId: number | null;
  onProjectChange: (id: number | null) => void;
  onSiteChange: (id: number | null) => void;
}

const errorRow = (message: string, onRetry: () => void) => (
  <div style={{ display: 'flex', alignItems: 'center', gap: 8, marginTop: 6 }}>
    <Typography.Text view="primary-small" color="negative" tag="span">
      {message}
    </Typography.Text>
    <button
      type="button"
      onClick={onRetry}
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
);

export const ImportForm = ({
  projectId,
  siteId,
  onProjectChange,
  onSiteChange,
}: Props) => {
  const projects = useProjects();
  const sites = useSites(projectId);

  const projectOptions = useMemo(
    () => projects.data.map((p) => ({ key: String(p.id), content: `${p.title} (${p.code})` })),
    [projects.data],
  );

  const siteOptions = useMemo(
    () =>
      sites.data.map((s) => ({
        key: String(s.id),
        content: [
          s.title,
          s.constructionPermissionNumber,
          s.stageNumber,
        ]
          .filter(Boolean)
          .join(' · '),
      })),
    [sites.data],
  );

  // Lazy-load: запрашиваем список проектов только при первом открытии Select.
  const handleProjectsOpen = (payload: { open?: boolean }) => {
    console.info(
      `[ImportForm] Select "Проект" onOpen — open=${payload.open}, status=${projects.status}`,
    );
    if (payload.open) projects.load();
  };

  const handleSitesOpen = (payload: { open?: boolean }) => {
    console.info(
      `[ImportForm] Select "Объект" onOpen — open=${payload.open}, status=${sites.status}, projectId=${projectId}`,
    );
    if (payload.open) sites.load();
  };

  const projectPlaceholder = (() => {
    switch (projects.status) {
      case 'loading':
        return 'Загрузка проектов…';
      case 'error':
        return 'Ошибка загрузки проектов';
      case 'success':
        return projects.data.length === 0 ? 'Проекты не найдены' : 'Выберите проект';
      default:
        return 'Нажмите для загрузки проектов';
    }
  })();

  const sitePlaceholder = (() => {
    if (!projectId) return 'Сначала выберите проект';
    switch (sites.status) {
      case 'loading':
        return 'Загрузка объектов…';
      case 'error':
        return 'Ошибка загрузки объектов';
      case 'success':
        return sites.data.length === 0 ? 'Объекты не найдены' : 'Выберите объект';
      default:
        return 'Нажмите для загрузки объектов';
    }
  })();

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
          disabled={projects.status === 'loading'}
          block
        />
        {projects.status === 'error' && projects.error
          ? errorRow(projects.error, projects.refetch)
          : null}
      </div>

      <div className="field">
        <Select
          label="Объект строительства"
          placeholder={sitePlaceholder}
          options={siteOptions}
          selected={siteId !== null ? String(siteId) : null}
          onChange={({ selected }) => onSiteChange(selected ? Number(selected.key) : null)}
          onOpen={handleSitesOpen}
          disabled={!projectId || sites.status === 'loading'}
          block
        />
        {sites.status === 'error' && sites.error
          ? errorRow(sites.error, sites.refetch)
          : null}
      </div>
    </div>
  );
};
