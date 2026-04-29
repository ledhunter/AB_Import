/**
 * Публичный API библиотеки методов Visary ListView.
 * Импортируйте отсюда — это барьер между ядром и потребителями (хуки, UI).
 */

// Generic-ядро
export { createListViewService, buildListViewRequestBody } from './createListViewService';
export { parseListViewResponse } from './parseListViewResponse';
export type {
  ListViewQuery,
  ListViewResult,
  ListViewService,
  ListViewServiceConfig,
  ListViewMapper,
  ListViewResponseRaw,
} from './types';

// Адаптеры сущностей
export { projectsService, toProjectItem, PROJECT_COLUMNS } from './entities/projects';
export {
  sitesService,
  toSiteItem,
  SITE_COLUMNS,
  buildSitesQueryByProject,
} from './entities/sites';
