// DEV-only логгер. Закрывает unsafe-formatstring (CWE-134, см. doc_project/121):
// конкатенация в console.* уходит в этот wrapper, в prod-bundle dev*-вызовы вырезаются.
// Бонус: меньше information disclosure (внутренние пути API не уходят в prod).
//
// boot*/critical* — отдельная категория «всегда-видно» (см. doc 148): они НЕ вырезаются
// в prod, потому что нужны для диагностики «белого экрана» на целевом стенде.
// Пишут с префиксом `[ab-fm-import]`, чтобы было видно в DevTools-консоли среди
// чужих сообщений (там обычно полно warning'ов от @alfalab/core-components).

const DEV = import.meta.env.DEV;
const TAG = '[ab-fm-import]';

export const devLog = (...args: unknown[]): void => {
  if (DEV) console.log(...args);
};

export const devInfo = (...args: unknown[]): void => {
  if (DEV) console.info(...args);
};

export const devWarn = (...args: unknown[]): void => {
  if (DEV) console.warn(...args);
};

export const devError = (...args: unknown[]): void => {
  if (DEV) console.error(...args);
};

export const devGroupCollapsed = (label: string): void => {
  if (DEV) console.groupCollapsed(label);
};

export const devGroupEnd = (): void => {
  if (DEV) console.groupEnd();
};

// ─── Всегда-видимые логи (для диагностики prod-инцидентов, см. doc 148) ───

export const bootLog = (...args: unknown[]): void => {
  console.info(TAG, ...args);
};

export const bootWarn = (...args: unknown[]): void => {
  console.warn(TAG, ...args);
};

export const criticalError = (...args: unknown[]): void => {
  console.error(TAG, ...args);
};
