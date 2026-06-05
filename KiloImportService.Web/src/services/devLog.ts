// DEV-only логгер. Закрывает unsafe-formatstring (CWE-134, см. doc_project/121):
// конкатенация в console.* уходит в этот wrapper, в prod-bundle вызовы вырезаются.
// Бонус: меньше information disclosure (внутренние пути API не уходят в prod).

const DEV = import.meta.env.DEV;

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
