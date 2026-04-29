import { useMemo, useState } from 'react';
import { Select } from '@alfalab/core-components/select';
import { Typography } from '@alfalab/core-components/typography';
import { useBackendProjects } from '../../hooks/useBackendProjects';
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

interface ProjectOption {
  key: string;
  content: string;
}

const toProjectOption = (p: { id: number; title: string; code: string }): ProjectOption => ({
  key: String(p.id),
  content: `${p.title} (${p.code})`,
});

export const ImportForm = ({
  projectId,
  siteId,
  onProjectChange,
  onSiteChange,
}: Props) => {
  // Контролируемая строка поиска для Select.searchProps.
  const [projectSearch, setProjectSearch] = useState('');
  // ⚠️ Сохраняем полную выбранную опцию (и id, и лябл), чтобы Select продолжал рисовать
  // выбранный проект даже когда search-фильтр исключил его из projects.data.
  const [selectedProject, setSelectedProject] = useState<ProjectOption | null>(null);

  const projects = useBackendProjects({ searchString: projectSearch, debounceMs: 300 });
  const sites = useSites(projectId);

  const projectOptions = useMemo(() => {
    const list = projects.data.map(toProjectOption);
    // Если выбранный проект отсутствует в текущем выводе search'a — всёравно включаем его в список,
    // иначе Select не сможет показать label при selected={key}.
    if (selectedProject && !list.some((o) => o.key === selectedProject.key)) {
      list.unshift(selectedProject);
    }
    return list;
  }, [projects.data, selectedProject]);

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

  // Первый open Select → backend синхронизирует кэш проектов с Visary (идемпотентно).
  const handleProjectsOpen = (payload: { open?: boolean }) => {
    console.info(
      `[ImportForm] Select "Проект" onOpen — open=${payload.open}, status=${projects.status}, warmed=${projects.isWarmed}`,
    );
    if (payload.open && !projects.isWarmed) projects.sync();
  };

  const handleSitesOpen = (payload: { open?: boolean }) => {
    console.info(
      `[ImportForm] Select "Объект" onOpen — open=${payload.open}, status=${sites.status}, projectId=${projectId}`,
    );
    if (payload.open) sites.load();
  };

  const projectPlaceholder = (() => {
    switch (projects.status) {
      case 'syncing':
        return 'Синхронизация проектов из Visary…';
      case 'loading':
        return 'Поиск проектов…';
      case 'error':
        return 'Ошибка загрузки проектов';
      case 'success':
        return projects.data.length === 0
          ? projectSearch
            ? `Ничего не найдено по «${projectSearch}»`
            : 'Проекты не найдены'
          : 'Выберите проект';
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
            if (selected) {
              const opt: ProjectOption = {
                key: String(selected.key),
                content: String(selected.content ?? selected.key),
              };
              setSelectedProject(opt);
              onProjectChange(Number(opt.key));
              // Очищаем поиск, чтобы при повторном открытии был полный список,
              // и Select не отображал старый запрос в поле.
              setProjectSearch('');
            } else {
              setSelectedProject(null);
              onProjectChange(null);
            }
            onSiteChange(null);
          }}
          onOpen={handleProjectsOpen}
          showSearch
          searchProps={{
            value: projectSearch,
            onChange: (value: string) => setProjectSearch(value),
            componentProps: { placeholder: 'Введите название или KK/ZPLM…' },
          }}
          disabled={projects.status === 'syncing'}
          block
        />
        {projects.fromFallback && projects.status === 'success' ? (
          <Typography.Text
            view="primary-small"
            color="secondary"
            tag="span"
          >
            Подгружено из Visary по запросу.
          </Typography.Text>
        ) : null}
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
