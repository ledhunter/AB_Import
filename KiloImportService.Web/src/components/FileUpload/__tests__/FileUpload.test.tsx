import { render, screen, fireEvent } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { FileUpload } from './FileUpload';

describe('FileUpload', () => {
  it('отображает кнопку выбора файла', () => {
    render(<FileUpload file={null} detectedFormat={null} onFileSelect={vi.fn()} />);
    expect(screen.getByText(/Перетащите файл сюда или нажмите/i)).toBeInTheDocument();
    expect(screen.getByText(/Поддерживаются: CSV, XLS, XLSB, XLSX/i)).toBeInTheDocument();
  });

  it('отображает загруженный файл', () => {
    const file = new File(['content'], 'test.xlsx', { type: 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet' });
    render(<FileUpload file={file} detectedFormat="Xlsx" onFileSelect={vi.fn()} />);
    expect(screen.getByText('test.xlsx')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /удалить/i })).toBeInTheDocument();
  });

  it('показывает ошибку при неподдерживаемом формате', () => {
    const onFileSelect = vi.fn();
    render(<FileUpload file={null} detectedFormat={null} onFileSelect={onFileSelect} />);

    const input = document.createElement('input');
    input.type = 'file';
    input.files = { 0: new File(['content'], 'test.txt', { type: 'text/plain' }) } as FileList;
    
    const event = new Event('change');
    Object.defineProperty(event, 'target', { value: input, enumerable: true });
    
    const container = document.createElement('div');
    const { container: rtlContainer } = render(<FileUpload file={null} detectedFormat={null} onFileSelect={onFileSelect} />);
    
    expect(rtlContainer.getByText(/Неподдерживаемый формат файла/i)).toBeInTheDocument();
  });

  it('вызывает onFileSelect при удалении файла', () => {
    const onFileSelect = vi.fn();
    const file = new File(['content'], 'test.xlsx', { type: 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet' });
    
    render(<FileUpload file={file} detectedFormat="Xlsx" onFileSelect={onFileSelect} />);
    
    const deleteBtn = screen.getByRole('button', { name: /удалить/i });
    deleteBtn.click();
    
    expect(onFileSelect).toHaveBeenCalledWith(null);
  });

  it('определяет формат файла', () => {
    const file = new File(['content'], 'test.csv', { type: 'text/csv' });
    render(<FileUpload file={file} detectedFormat="Csv" onFileSelect={vi.fn()} />);
    expect(screen.getByText(/Определён формат/i)).toBeInTheDocument();
    expect(screen.getByText('CSV')).toBeInTheDocument();
  });
});
