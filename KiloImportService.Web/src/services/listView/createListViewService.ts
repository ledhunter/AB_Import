import { visaryPost } from '../visaryApi';
import { parseListViewResponse } from './parseListViewResponse';
import type {
  ListViewQuery,
  ListViewResponseRaw,
  ListViewResult,
  ListViewService,
  ListViewServiceConfig,
} from './types';

/**
 * Базовая структура тела запроса к Visary ListView API.
 * Имена полей (PascalCase) соответствуют контракту Visary.
 */
interface ListViewRequestBody {
  Mnemonic: string;
  PageSkip: number;
  PageSize: number;
  Columns: string[];
  Sorts: string;
  Hidden: boolean;
  ExtraFilter: string | null;
  SearchString: string;
  AssociationFilter?: {
    AssociatedId: number;
    Filters: unknown | null;
  } | null;
}

const DEFAULT_PAGE_SIZE = 50;
const DEFAULT_SORT = '[{"selector":"ID","desc":true}]';

/**
 * Собирает тело запроса Visary ListView из конфига сервиса и параметров вызова.
 * Вынесено отдельно, чтобы тестировать без сетевых вызовов.
 */
export function buildListViewRequestBody<TRaw, TItem>(
  config: ListViewServiceConfig<TRaw, TItem>,
  query: ListViewQuery = {},
): ListViewRequestBody {
  return {
    Mnemonic: config.mnemonic,
    PageSkip: query.pageSkip ?? 0,
    PageSize: query.pageSize ?? config.defaultPageSize ?? DEFAULT_PAGE_SIZE,
    Columns: config.columns,
    Sorts: query.sorts ?? config.defaultSort ?? DEFAULT_SORT,
    Hidden: false,
    ExtraFilter: query.extraFilter ?? null,
    SearchString: query.searchString ?? '',
    AssociationFilter: query.associationFilter ?? null,
  };
}

/**
 * Фабрика сервисов для Visary ListView API.
 *
 * @example
 *   const projectsService = createListViewService<ConstructionProjectRaw, ProjectItem>({
 *     mnemonic: 'constructionproject',
 *     columns: PROJECT_COLUMNS,
 *     toItem: toProjectItem,
 *     logTag: '[projects]',
 *   });
 *
 *   const { items, totalCount } = await projectsService.fetch({ pageSize: 50 });
 */
export function createListViewService<TRaw, TItem>(
  config: ListViewServiceConfig<TRaw, TItem>,
): ListViewService<TItem> {
  const path = `/listview/${config.mnemonic}${config.pathSuffix ?? ''}`;
  const logTag = config.logTag ?? `[${config.mnemonic}]`;

  async function fetch(query: ListViewQuery = {}): Promise<ListViewResult<TItem>> {
    const body = buildListViewRequestBody(config, query);
    
    // Для эндпоинта /onetomany/Project нужен query parameter associationId
    const queryParams = query.associationFilter?.AssociatedId
      ? { associationId: query.associationFilter.AssociatedId }
      : undefined;
    
    console.groupCollapsed(`${logTag} запрос к ${path}`);
    console.log('Query:', query);
    console.log('Query params:', queryParams);
    console.log('Request body:', JSON.stringify(body, null, 2));
    console.groupEnd();
    
    const raw = await visaryPost<ListViewResponseRaw<TRaw>>(path, body, {
      signal: query.signal,
      queryParams,
    });
    const result = parseListViewResponse(raw, config.toItem);
    console.info(
      `${logTag} fetched ${result.items.length} of ${result.totalCount} (mnemonic=${config.mnemonic})`,
    );
    return result;
  }

  return {
    mnemonic: config.mnemonic,
    logTag,
    fetch,
  };
}
