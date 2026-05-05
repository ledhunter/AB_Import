import { render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import userEvent from '@testing-library/user-event';
import { ImportTypePicker } from './ImportTypePicker';

describe('ImportTypePicker', () => {
  it('отображает placeholder при загрузке', () => {
    vi.doUnmock('./hooks/useImportTypes');
    vi.mock('../../hooks/useImportTypes', () => ({
      useImportTypes: () => ({
        data: [],
        status: 'loading',
        error: null,
        refetch: vi.fn(),
      }),
    }));

    render(<ImportTypePicker value={null} onChange={vi.fn()} />);
    expect(screen.getByPlaceholderText(/Загрузка типов импорта/i)).toBeInTheDocument();
  });

  it('отображает опции типов импорта', () => {
    vi.doUnmock('./hooks/useImportTypes');
    vi.mock('../../hooks/useImportTypes', () => ({
      useImportTypes: () => ({
        data: [
          { id: 'rooms', label: 'Помещения', isImplemented: true },
          { id: 'finmodel', label: 'Финмодель', isImplemented: true },
        ],
        status: 'success',
        error: null,
        refetch: vi.fn(),
      }),
    }));

    render(<ImportTypePicker value={null} onChange={vi.fn()} />);
    expect(screen.getByRole('combobox', { name: 'Тип импорта' })).toBeInTheDocument();
  });

  it('вызывает onChange при выборе типа', async () => {
    const onChange = vi.fn();
    vi.doUnmock('./hooks/useImportTypes');
    vi.mock('../../hooks/useImportTypes', () => ({
      useImportTypes: () => ({
        data: [{ id: 'rooms', label: 'Помещения', isImplemented: true }],
        status: 'success',
        error: null,
        refetch: vi.fn(),
      }),
    }));

    render(<ImportTypePicker value={null} onChange={onChange} />);
    const select = screen.getByRole('combobox', { name: 'Тип импорта' });
    await userEvent.click(select);
    expect(onChange).toHaveBeenCalled();
  });

  it('дезавилирует не реализованные типы', () => {
    vi.doUnmock('./hooks/useImportTypes');
    vi.mock('../../hooks/useImportTypes', () => ({
      useImportTypes: () => ({
        data: [
          { id: 'rooms', label: 'Помещения', isImplemented: true },
          { id: 'unused', label: 'Устаревший', isImplemented: false },
        ],
        status: 'success',
        error: null,
        refetch: vi.fn(),
      }),
    }));

    render(<ImportTypePicker value={null} onChange={vi.fn()} />);
    expect(screen.getByText(/Помещения/i)).toBeInTheDocument();
    expect(screen.getByText(/Устаревший · скоро/i)).toBeInTheDocument();
  });
});
