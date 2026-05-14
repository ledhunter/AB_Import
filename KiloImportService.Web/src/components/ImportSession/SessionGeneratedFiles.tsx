import { useState } from 'react';
import { Button } from '@alfalab/core-components/button';
import { Typography } from '@alfalab/core-components/typography';
import { downloadBlob } from '../../utils/downloadBlob';
import { ImportsApiError } from '../../services/importsService';
import type { UiGeneratedFile } from '../../types/session';

interface Props {
  files: UiGeneratedFile[];
}

/**
 * Раздел «Сформированные файлы» на детальной странице сессии.
 *
 * Источник списка — поле `generatedFiles` из ответа `GET /api/imports/{id}`.
 * Backend сам решает, какие файлы доступны для скачивания (см. `BuildGeneratedFilesAsync`
 * в ImportsController; критерий доступности — наличие нужных staged-строк).
 *
 * Каждый элемент может быть либо:
 * • файлом для скачивания (`downloadUrl`) — paттерн как у PDF-экспорта
 *   (см. doc_project/74-import-pdf-export.md): fetch → Blob → <a download>;
 * • action-операцией (`actionUrl`) — POST на backend без скачивания, например
 *   «Загрузить бюджет в Visary» (kind="budget-upload", см. doc_project/82-…).
 *   Возвращает JSON с результатом (typedImportWbsId и т.п.), выводим toast.
 *
 * Если файлов нет — компонент не рендерит ничего (раздел не появляется в DOM),
 * чтобы не показывать пустую секцию для типов импорта без артефактов.
 */
export const SessionGeneratedFiles = ({ files }: Props) => {
  const [busyKey, setBusyKey] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [successMessage, setSuccessMessage] = useState<string | null>(null);

  if (files.length === 0) return null;

  const handleDownload = async (file: UiGeneratedFile) => {
    if (busyKey || !file.downloadUrl) return;
    setBusyKey(file.kind);
    setError(null);
    setSuccessMessage(null);
    try {
      const response = await fetch(file.downloadUrl, { method: 'GET' });
      if (!response.ok) {
        throw await buildApiError(response);
      }
      const blob = await response.blob();
      downloadBlob(blob, file.fileName);
    } catch (err) {
      setError(`Не удалось скачать «${file.label}»: ${formatError(err)}`);
    } finally {
      setBusyKey(null);
    }
  };

  const handleAction = async (file: UiGeneratedFile) => {
    if (busyKey || !file.actionUrl) return;
    setBusyKey(file.kind);
    setError(null);
    setSuccessMessage(null);
    try {
      const response = await fetch(file.actionUrl, { method: 'POST' });
      if (!response.ok) {
        throw await buildApiError(response);
      }
      // Тело ответа — JSON с деталями операции (для budget-upload: typedImportWbsId и т.п.).
      const body = await response.json().catch(() => null);
      setSuccessMessage(buildSuccessMessage(file, body));
    } catch (err) {
      setError(`«${file.label}» — ошибка: ${formatError(err)}`);
    } finally {
      setBusyKey(null);
    }
  };

  return (
    <div className="generated-files">
      <Typography.Title view="xsmall" tag="h3" weight="bold" style={{ margin: '0 0 12px' }}>
        Сформированные файлы
      </Typography.Title>
      <Typography.Text
        view="primary-small"
        color="secondary"
        tag="div"
        style={{ marginBottom: 12 }}
      >
        Файлы для проверки и ручного импорта в целевые системы. Сгенерируются заново при каждом скачивании.
      </Typography.Text>

      <ul className="generated-files__list" style={{ listStyle: 'none', padding: 0, margin: 0 }}>
        {files.map((file) => {
          const isAction = !file.downloadUrl && !!file.actionUrl;
          const buttonLabel = isAction ? 'Загрузить' : 'Скачать';
          const onClick = isAction ? () => handleAction(file) : () => handleDownload(file);
          return (
            <li
              key={file.kind}
              className="generated-files__item"
              style={{
                display: 'flex',
                alignItems: 'center',
                gap: 16,
                padding: '12px 0',
                borderTop: '1px solid var(--color-light-graphic-secondary, #d8d8d8)',
              }}
            >
              <div style={{ flex: 1, minWidth: 0 }}>
                <Typography.Text view="primary-medium" weight="bold" tag="div">
                  {file.label}
                </Typography.Text>
                {file.description && (
                  <Typography.Text
                    view="primary-small"
                    color="secondary"
                    tag="div"
                    style={{ marginTop: 2 }}
                  >
                    {file.description}
                  </Typography.Text>
                )}
                <Typography.Text
                  view="primary-small"
                  color="secondary"
                  tag="div"
                  style={{ marginTop: 2 }}
                >
                  {file.fileName}
                </Typography.Text>
              </div>
              <Button
                view={isAction ? 'primary' : 'secondary'}
                size={40}
                onClick={onClick}
                loading={busyKey === file.kind}
                disabled={busyKey !== null && busyKey !== file.kind}
              >
                {buttonLabel}
              </Button>
            </li>
          );
        })}
      </ul>

      {error && (
        <Typography.Text view="primary-small" color="negative" tag="div" style={{ marginTop: 12 }}>
          {error}
        </Typography.Text>
      )}
      {successMessage && (
        <Typography.Text view="primary-small" color="positive" tag="div" style={{ marginTop: 12 }}>
          {successMessage}
        </Typography.Text>
      )}
    </div>
  );
};

async function buildApiError(response: Response): Promise<ImportsApiError> {
  const text = await response.text().catch(() => '');
  let serverMessage = '';
  try {
    const parsed = text ? JSON.parse(text) : null;
    if (parsed && typeof parsed === 'object' && 'error' in parsed) {
      serverMessage = String((parsed as Record<string, unknown>).error);
    }
  } catch {
    /* ignore non-JSON body */
  }
  return new ImportsApiError(
    serverMessage || `Backend вернул ${response.status} ${response.statusText}`,
    response.status,
    text,
  );
}

function formatError(err: unknown): string {
  return err instanceof Error ? err.message : String(err);
}

function buildSuccessMessage(file: UiGeneratedFile, body: unknown): string {
  // Для budget-upload backend возвращает {typedImportWbsId, fileStorageItemId}.
  if (
    file.kind === 'budget-upload'
    && body
    && typeof body === 'object'
    && 'typedImportWbsId' in body
  ) {
    const id = (body as Record<string, unknown>).typedImportWbsId;
    return `Бюджет залит в Visary, задание импорта typedimportwbs создано (ID ${id}). Импорт обрабатывается на стороне Visary.`;
  }
  return `«${file.label}» — выполнено.`;
}
