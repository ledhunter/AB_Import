import { afterEach, describe, expect, it, vi } from 'vitest';
import { safeFetch, __isAllowedPathForTests } from '../safeFetch';

afterEach(() => {
  vi.restoreAllMocks();
});

describe('safeFetch — whitelist guard', () => {
  it('пропускает точный API-корень', () => {
    expect(__isAllowedPathForTests('/api/imports')).toBe(true);
    expect(__isAllowedPathForTests('/api/visary')).toBe(true);
    expect(__isAllowedPathForTests('/api/sites')).toBe(true);
    expect(__isAllowedPathForTests('/api/projects')).toBe(true);
    expect(__isAllowedPathForTests('/hubs')).toBe(true);
  });

  it('пропускает суб-путь с / или ?', () => {
    expect(__isAllowedPathForTests('/api/imports/sessions/abc')).toBe(true);
    expect(__isAllowedPathForTests('/api/visary/crud/room/123')).toBe(true);
    expect(__isAllowedPathForTests('/api/sites/sync/1?projectId=2')).toBe(true);
    expect(__isAllowedPathForTests('/api/imports?q=1')).toBe(true);
    expect(__isAllowedPathForTests('/hubs/imports')).toBe(true);
  });

  it('пропускает /api/import-types (отдельный корень)', () => {
    expect(__isAllowedPathForTests('/api/import-types')).toBe(true);
  });

  it('отклоняет похожий, но не whitelisted prefix', () => {
    // `/api/imports-evil` не должен пройти как `/api/imports`
    expect(__isAllowedPathForTests('/api/imports-evil/x')).toBe(false);
    expect(__isAllowedPathForTests('/api/visaryX')).toBe(false);
  });

  it('отклоняет произвольные пути вне whitelist', () => {
    expect(__isAllowedPathForTests('/etc/passwd')).toBe(false);
    expect(__isAllowedPathForTests('/admin')).toBe(false);
    expect(__isAllowedPathForTests('/api/secret')).toBe(false);
  });
});

describe('safeFetch — runtime invariants', () => {
  it('кидает TypeError для пустой строки', () => {
    expect(() => safeFetch('')).toThrow(TypeError);
  });

  it('кидает для не-string url', () => {
    expect(() => safeFetch(undefined as unknown as string)).toThrow(TypeError);
    expect(() => safeFetch(null as unknown as string)).toThrow(TypeError);
  });

  // safeFetch выбрасывает синхронно (до возврата Promise), поэтому используем
  // `expect(() => …).toThrow`, а не `.rejects.toThrow`.

  it('кидает для абсолютного URL (cross-origin)', () => {
    expect(() => safeFetch('https://evil.com/api/imports')).toThrow(/same-origin/);
  });

  it('кидает для protocol-relative URL', () => {
    expect(() => safeFetch('//evil.com/api/imports')).toThrow(/protocol-relative/);
  });

  it('кидает для path traversal', () => {
    expect(() => safeFetch('/api/imports/../etc/passwd')).toThrow(/traversal/);
    expect(() => safeFetch('/api/imports/./x')).toThrow(/traversal/);
  });

  it('кидает для URL вне whitelist', () => {
    expect(() => safeFetch('/api/secret')).toThrow(/whitelist/);
  });

  it('делегирует в global fetch при разрешённом URL', async () => {
    const mockResponse = new Response('ok', { status: 200 });
    const spy = vi.spyOn(globalThis, 'fetch').mockResolvedValue(mockResponse);

    const res = await safeFetch('/api/imports/sessions/123', { method: 'GET' });

    expect(spy).toHaveBeenCalledTimes(1);
    expect(spy).toHaveBeenCalledWith('/api/imports/sessions/123', { method: 'GET' });
    expect(res).toBe(mockResponse);
  });

  it('пробрасывает signal/headers/body в init без изменений', async () => {
    const spy = vi.spyOn(globalThis, 'fetch').mockResolvedValue(new Response());
    const controller = new AbortController();
    const init: RequestInit = {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', Authorization: 'Bearer x' },
      body: JSON.stringify({ foo: 'bar' }),
      signal: controller.signal,
    };

    await safeFetch('/api/visary/crud/room', init);

    expect(spy).toHaveBeenCalledTimes(1);
    expect(spy).toHaveBeenCalledWith('/api/visary/crud/room', init);
  });
});
