import { useRef, useState } from 'react';
import { FileUploadItem } from '@alfalab/core-components/file-upload-item';
import { Typography } from '@alfalab/core-components/typography';
import { Status } from '@alfalab/core-components/status';
import { ACCEPT_ALL_SUPPORTED, detectFileFormat } from '../../utils/fileFormat';
import type { FileFormat } from '../../types/import';

interface Props {
  file: File | null;
  detectedFormat: FileFormat | null;
  onFileSelect: (file: File | null) => void;
}

export const FileUpload = ({ file, detectedFormat, onFileSelect }: Props) => {
  const inputRef = useRef<HTMLInputElement>(null);
  const [error, setError] = useState<string | null>(null);

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

  return (
    <div className="field">
      <Typography.Text view="primary-medium" weight="bold" tag="div" style={{ marginBottom: 12 }}>
        Загрузите файл
      </Typography.Text>

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
          <input
            ref={inputRef}
            type="file"
            accept={ACCEPT_ALL_SUPPORTED}
            onChange={handleChange}
            style={{ display: 'none' }}
          />
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
            onDelete={() => onFileSelect(null)}
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
        </div>
      )}
    </div>
  );
};
