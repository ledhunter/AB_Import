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
 * Если файлов нет — компонент не рендерит ничего (раздел не появляется в DOM),
 * чтобы не показывать пустую секцию для типов импорта, у которых нет артефактов.
 *
 * Скачивание идёт через `fetch → Blob → <a download>` (не прямой `<a href>`),
 * чтобы корректно показывать индикатор загрузки и ошибки в UI. Тот же паттерн,
 * что и у PDF-экспорта (см. doc_project/74-import-pdf-export.md).
 */
export const SessionGeneratedFiles = ({ files }: Props) => {
  const [busyKey, setBusyKey] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  if (files.length === 0) return null;

  const handleDownload = async (file: UiGeneratedFile) => {
    if (busyKey) return;
    setBusyKey(file.kind);
    setError(null);
    try {
      const response = await fetch(file.downloadUrl, { method: 'GET' });
      if (!response.ok) {
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
        throw new ImportsApiError(
          serverMessage || `Backend вернул ${response.status} ${response.statusText}`,
          response.status,
          text,
        );
      }
      const blob = await response.blob();
      downloadBlob(blob, file.fileName);
    } catch (err) {
      const message = err instanceof Error ? err.message : String(err);
      setError(`Не удалось скачать «${file.label}»: ${message}`);
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
        {files.map((file) => (
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
              view="secondary"
              size={40}
              onClick={() => handleDownload(file)}
              loading={busyKey === file.kind}
              disabled={busyKey !== null && busyKey !== file.kind}
            >
              Скачать
            </Button>
          </li>
        ))}
      </ul>

      {error && (
        <Typography.Text view="primary-small" color="negative" tag="div" style={{ marginTop: 12 }}>
          {error}
        </Typography.Text>
      )}
    </div>
  );
};
