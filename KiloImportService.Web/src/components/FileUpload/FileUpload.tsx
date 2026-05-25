import { useRef, useState } from 'react';
import { FileUploadItem } from '@alfalab/core-components/file-upload-item';
import { Typography } from '@alfalab/core-components/typography';
import { Status } from '@alfalab/core-components/status';
import { Button } from '@alfalab/core-components/button';
import { ACCEPT_ALL_SUPPORTED, detectFileFormat } from '../../utils/fileFormat';
import type { FileFormat } from '../../types/import';

interface Props {
  file: File | null;
  detectedFormat: FileFormat | null;
  onFileSelect: (file: File | null) => void;
  /**
   * Опциональный второй файл (на сегодня — только Финмодель). Если переданы
   * `secondaryFile`/`onSecondaryFileSelect`, под основным uploader-ом отрисовывается
   * компактный второй слот с подписью {@link secondaryLabel}.
   * См. doc_project/110-finmodel-plan-and-fmmodel.md.
   */
  secondaryFile?: File | null;
  onSecondaryFileSelect?: (file: File | null) => void;
  secondaryLabel?: string;
  secondaryHint?: string;
}

export const FileUpload = ({
  file,
  detectedFormat,
  onFileSelect,
  secondaryFile,
  onSecondaryFileSelect,
  secondaryLabel = 'Дополнительный файл (опционально)',
  secondaryHint,
}: Props) => {
  const inputRef = useRef<HTMLInputElement>(null);
  const secondaryInputRef = useRef<HTMLInputElement>(null);
  const [error, setError] = useState<string | null>(null);
  const showSecondary = typeof onSecondaryFileSelect === 'function';

  const validateAndSelect = (f: File | null | undefined) => {
    if (!f) return;
    const fmt = detectFileFormat(f.name);
    if (!fmt) {
      setError(`Неподдерживаемый формат файла. Допустимые: CSV, XLS, XLSB, XLSX.`);
      onFileSelect(null);
      return;
    }
    setError(null);
    onFileSelect(f);
  };

  const handleChange = (e: React.ChangeEvent<HTMLInputElement>) => validateAndSelect(e.target.files?.[0]);
  const handleClick = () => inputRef.current?.click();
  const handleRemove = () => {
    setError(null);
    if (inputRef.current) inputRef.current.value = '';
    onFileSelect(null);
  };

  // Второй (опциональный) файл: формат не валидируем — backend сам решит, как
  // его трактовать (FinModel ждёт XLSX с листом «План»). Здесь только UX.
  const handleSecondaryChange = (e: React.ChangeEvent<HTMLInputElement>) => {
    const f = e.target.files?.[0] ?? null;
    onSecondaryFileSelect?.(f);
  };
  const handleSecondaryClick = () => secondaryInputRef.current?.click();
  const handleSecondaryRemove = () => {
    if (secondaryInputRef.current) secondaryInputRef.current.value = '';
    onSecondaryFileSelect?.(null);
  };

  return (
    <div className="field">
      <Typography.Text view="primary-medium" weight="bold" tag="div" style={{ marginBottom: 12 }}>
        Загрузите файл
      </Typography.Text>

      <input
        ref={inputRef}
        type="file"
        accept={ACCEPT_ALL_SUPPORTED}
        onChange={handleChange}
        style={{ display: 'none' }}
      />

      {!file ? (
        <>
          <div style={{
            border: '2px dashed #d8d8d8',
            borderRadius: '12px',
            padding: '40px 20px',
            textAlign: 'center',
            backgroundColor: error ? '#fef4f4' : '#fafbfc',
            cursor: 'pointer',
          }} onClick={handleClick}>
            <Typography.Text view="primary-medium" color="primary" tag="div">
              Перетащите файл сюда или нажмите для выбора
            </Typography.Text>
            <Typography.Text view="primary-small" color="secondary" tag="div" style={{ marginTop: 4 }}>
              Поддерживаются: CSV, XLS, XLSB, XLSX · Макс. размер: 50 МБ
            </Typography.Text>
          </div>
          {error && (
            <Typography.Text view="primary-small" color="negative" tag="div" style={{ marginTop: 8 }}>
              {error}
            </Typography.Text>
          )}
        </>
      ) : (
        <div className="file-uploaded">
          <FileUploadItem
            title={file.name}
            size={file.size}
            uploadStatus="UPLOADED"
            showDelete
            onDelete={handleRemove}
          />
          {detectedFormat && (
            <div className="file-uploaded__format">
              <Typography.Text view="primary-small" color="secondary" tag="span">
                Определён формат:
              </Typography.Text>
              <Status color="blue" view="muted-alt">
                {detectedFormat.toUpperCase()}
              </Status>
            </div>
          )}
          <div className="file-uploaded__actions">
            <Button view="secondary" size={40} onClick={handleRemove}>
              Удалить файл
            </Button>
            <Button view="tertiary" size={40} onClick={handleClick}>
              Выбрать другой
            </Button>
          </div>
        </div>
      )}

      {showSecondary && (
        <div style={{ marginTop: 16 }}>
          <Typography.Text view="primary-medium" weight="bold" tag="div" style={{ marginBottom: 8 }}>
            {secondaryLabel}
          </Typography.Text>
          {secondaryHint && (
            <Typography.Text
              view="primary-small"
              color="secondary"
              tag="div"
              style={{ marginBottom: 8 }}
            >
              {secondaryHint}
            </Typography.Text>
          )}
          <input
            ref={secondaryInputRef}
            type="file"
            accept={ACCEPT_ALL_SUPPORTED}
            onChange={handleSecondaryChange}
            style={{ display: 'none' }}
          />
          {!secondaryFile ? (
            <Button view="secondary" size={40} onClick={handleSecondaryClick}>
              Прикрепить файл
            </Button>
          ) : (
            <div className="file-uploaded">
              <FileUploadItem
                title={secondaryFile.name}
                size={secondaryFile.size}
                uploadStatus="UPLOADED"
                showDelete
                onDelete={handleSecondaryRemove}
              />
              <div className="file-uploaded__actions">
                <Button view="secondary" size={40} onClick={handleSecondaryRemove}>
                  Удалить файл
                </Button>
                <Button view="tertiary" size={40} onClick={handleSecondaryClick}>
                  Выбрать другой
                </Button>
              </div>
            </div>
          )}
        </div>
      )}
    </div>
  );
};
