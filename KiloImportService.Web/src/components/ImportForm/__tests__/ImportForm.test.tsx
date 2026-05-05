import { describe, expect, it } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { ImportForm } from './ImportForm';

describe('ImportForm', () => {
  it('отображает Select для выбора проекта', () => {
    render(
      <ImportForm
        projectId={null}
        siteId={null}
        onProjectChange={jest.fn()}
        onSiteChange={jest.fn()}
      />
    );

    expect(screen.getByRole('combobox', { name: 'Проект' })).toBeInTheDocument();
  });

  it('отображает Select для выбора объекта строительства', () => {
    render(
      <ImportForm
        projectId={1}
        siteId={null}
        onProjectChange={jest.fn()}
        onSiteChange={jest.fn()}
      />
    );

    expect(screen.getByRole('combobox', { name: 'Объект строительства' })).toBeInTheDocument();
  });

  it('вызывает onSiteChange при выборе объекта', async () => {
    const onSiteChange = jest.fn();
    render(
      <ImportForm
        projectId={1}
        siteId={null}
        onProjectChange={jest.fn()}
        onSiteChange={onSiteChange}
      />
    );

    const siteSelect = screen.getByRole('combobox', { name: 'Объект строительства' });
    await userEvent.click(siteSelect);

    expect(onSiteChange).not.toHaveBeenCalled();
  });
});
