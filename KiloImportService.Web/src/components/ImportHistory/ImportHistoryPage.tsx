/**
 * Страница «История импортов»:
 *   - Список всех завершённых/активных сессий из `GET /api/imports`
 *   - Фильтры: статус и тип импорта
 *   - Пагинация (skip/take, отсортировано по StartedAt DESC)
 *   - Клик по строке → детальный просмотр (SessionSummary + SessionRowsTable),
 *     read-only без apply/cancel и без SignalR-подписки.
 *
 * Маршрутизация внутри страницы — простой state: либо список, либо открытая сессия.
 * Глобальная навигация (вкладки «Импорт» / «История») — в `App.tsx`.
 */

import { useCallback, useState } from 'react';
import { Button } from '@alfalab/core-components/button';
import { Typography } from '@alfalab/core-components/typography';
import { HistoryFilters } from './HistoryFilters';
import { HistorySessionsTable } from './HistorySessionsTable';
import { HistoryPagination } from './HistoryPagination';
import { HistoryDetailView } from './HistoryDetailView';
import { useImportsHistory } from '../../hooks/useImportsHistory';
import { useImportTypes } from '../../hooks/useImportTypes';
import { exportImportsPdf, ImportsApiError } from '../../services/importsService';
import { downloadBlob } from '../../utils/downloadBlob';

export const ImportHistoryPage = () => {
  const history = useImportsHistory();
  const importTypes = useImportTypes();
  const [openedSessionId, setOpenedSessionId] = useState<string | null>(null);
  const [selectedIds, setSelectedIds] = useState<Set<string>>(() => new Set());
  const [exporting, setExporting] = useState(false);
  const [exportError, setExportError] = useState<string | null>(null);

  const toggleOne = useCallback((sessionId: string) => {
    setSelectedIds((prev) => {
      const next = new Set(prev);
      if (next.has(sessionId)) next.delete(sessionId);
      else next.add(sessionId);
      return next;
    });
  }, []);

  const toggleAllOnPage = useCallback(
    (select: boolean) => {
      setSelectedIds((prev) => {
        const next = new Set(prev);
        for (const item of history.items) {
          if (select) next.add(item.sessionId);
          else next.delete(item.sessionId);
        }
        return next;
      });
    },
    [history.items],
  );

  const clearSelection = useCallback(() => setSelectedIds(new Set()), []);

  const handleExport = useCallback(async () => {
    if (selectedIds.size === 0 || exporting) return;
    setExporting(true);
    setExportError(null);
    try {
      const blob = await exportImportsPdf(Array.from(selectedIds));
      const ts = new Date()
        .toISOString()
        .replace(/[-:]/g, '')
        .replace('T', '-')
        .slice(0, 15);
      downloadBlob(blob, `imports-${ts}.pdf`);
    } catch (err) {
      const message =
        err instanceof ImportsApiError
          ? err.message
          : err instanceof Error
            ? err.message
            : String(err);
      setExportError(message);
    } finally {
      setExporting(false);
    }
  }, [selectedIds, exporting]);

  if (openedSessionId) {
    return (
      <HistoryDetailView
        sessionId={openedSessionId}
        importTypes={importTypes.data}
        onBack={() => setOpenedSessionId(null)}
      />
    );
  }

  return (
    <div className="card">
      <div className="history-header">
        <Typography.Title view="small" tag="h2" weight="bold" style={{ margin: 0 }}>
          История импортов
        </Typography.Title>

        <div className="history-header__actions">
          {selectedIds.size > 0 && (
            <Button view="text" size={40} onClick={clearSelection}>
              Снять выбор
            </Button>
          )}
          <Button
            view="primary"
            size={40}
            disabled={selectedIds.size === 0}
            loading={exporting}
            onClick={handleExport}
          >
            Выгрузить в PDF{selectedIds.size > 0 ? ` (${selectedIds.size})` : ''}
          </Button>
        </div>
      </div>

      {exportError && (
        <div className="messages messages--error" style={{ marginTop: 12 }}>
          <Typography.Text view="primary-medium" weight="bold" tag="div">
            Не удалось сформировать PDF
          </Typography.Text>
          <Typography.Text view="primary-small" tag="div" style={{ marginTop: 4 }}>
            {exportError}
          </Typography.Text>
        </div>
      )}

      <div style={{ marginTop: 16 }}>
        <HistoryFilters
          status={history.query.status}
          importTypeCode={history.query.importTypeCode}
          projectId={history.query.projectId}
          importTypes={importTypes.data}
          total={history.total}
          loading={history.loading}
          onStatusChange={(status) => history.setFilters({ status })}
          onTypeChange={(importTypeCode) => history.setFilters({ importTypeCode })}
          onProjectChange={(projectId) => history.setFilters({ projectId })}
          onRefresh={() => void history.refresh()}
        />
      </div>

      <div className="section-gap" />

      <HistorySessionsTable
        items={history.items}
        loading={history.loading}
        error={history.error}
        importTypes={importTypes.data}
        selectedIds={selectedIds}
        hasActiveFilters={
          history.query.status != null ||
          history.query.importTypeCode != null ||
          history.query.projectId != null
        }
        onOpen={setOpenedSessionId}
        onToggleOne={toggleOne}
        onToggleAllOnPage={toggleAllOnPage}
      />

      <HistoryPagination
        skip={history.query.skip}
        take={history.query.take}
        total={history.total}
        onChange={(skip) => history.setFilters({ skip })}
      />
    </div>
  );
};
