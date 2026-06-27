import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { safeFetch, __isAllowedPathForTests } from '../safeFetch';

// ────────────────────────────────────────────────────────────────────────────
// XHR mock. После v6-перехода (см. doc 121 § AppSec v6) `safeFetch` реализован
// поверх `XMLHttpRequest`, а не `fetch`. Mock'аем globalThis.XMLHttpRequest
// классом, у которого можно перехватить open/send/headers и эмитить onload.
// ────────────────────────────────────────────────────────────────────────────

interface MockXhrCall {
  method: string;
  url: string;
  async: boolean;
  headers: Record<string, string>;
  body: unknown;
}

class MockXhr {
  static lastCall: MockXhrCall | null = null;
  static autoLoad: { status: number; statusText: string; responseHeaders: string; response: unknown } | null = {
    status: 200,
    statusText: 'OK',
    responseHeaders: 'content-type: application/json',
    response: new ArrayBuffer(0),
  };

  public responseType: XMLHttpRequestResponseType = '';
  public onload: (() => void) | null = null;
  public onerror: (() => void) | null = null;
  public onabort: (() => void) | null = null;
  public ontimeout: (() => void) | null = null;
  public status = 0;
  public statusText = '';
  public response: unknown = null;
  private responseHeaders = '';
  private call: Partial<MockXhrCall> = { headers: {} };

  open(method: string, url: string, async: boolean) {
    this.call.method = method;
    this.call.url = url;
    this.call.async = async;
  }

  setRequestHeader(name: string, value: string) {
    (this.call.headers as Record<string, string>)[name] = value;
  }

  getAllResponseHeaders() {
    return this.responseHeaders;
  }

  abort() {
    if (this.onabort) this.onabort();
  }

  send(body: unknown) {
    this.call.body = body;
    MockXhr.lastCall = this.call as MockXhrCall;
    if (MockXhr.autoLoad) {
      this.status = MockXhr.autoLoad.status;
      this.statusText = MockXhr.autoLoad.statusText;
      this.responseHeaders = MockXhr.autoLoad.responseHeaders;
      this.response = MockXhr.autoLoad.response;
      // emit async — как реальный XHR
      queueMicrotask(() => {
        if (this.onload) this.onload();
      });
    }
  }
}

beforeEach(() => {
  MockXhr.lastCall = null;
  MockXhr.autoLoad = {
    status: 200,
    statusText: 'OK',
    responseHeaders: 'content-type: application/json',
    response: new ArrayBuffer(0),
  };
  vi.stubGlobal('XMLHttpRequest', MockXhr);
});

afterEach(() => {
  vi.unstubAllGlobals();
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

  it('делегирует в XMLHttpRequest при разрешённом URL', async () => {
    const res = await safeFetch('/api/imports/sessions/123', { method: 'GET' });

    expect(MockXhr.lastCall).not.toBeNull();
    expect(MockXhr.lastCall?.method).toBe('GET');
    expect(MockXhr.lastCall?.url).toBe('/api/imports/sessions/123');
    expect(res).toBeInstanceOf(Response);
    expect(res.status).toBe(200);
  });

  it('пробрасывает signal/headers/body в XHR без изменений', async () => {
    const controller = new AbortController();
    const init: RequestInit = {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', Authorization: 'Bearer x' },
      body: JSON.stringify({ foo: 'bar' }),
      signal: controller.signal,
    };

    await safeFetch('/api/visary/crud/room', init);

    expect(MockXhr.lastCall?.method).toBe('POST');
    expect(MockXhr.lastCall?.url).toBe('/api/visary/crud/room');
    expect(MockXhr.lastCall?.headers).toEqual({
      'Content-Type': 'application/json',
      Authorization: 'Bearer x',
    });
    expect(MockXhr.lastCall?.body).toBe('{"foo":"bar"}');
  });
});
