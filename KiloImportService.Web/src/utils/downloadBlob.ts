/**
 * Триггерит скачивание blob-а как файла через временный `<a download>`.
 * Используется для PDF-отчётов и других бинарных ответов backend'а.
 */
export function downloadBlob(blob: Blob, fileName: string): void {
  const url = URL.createObjectURL(blob);
  try {
    const a = document.createElement('a');
    a.href = url;
    a.download = fileName;
    a.style.display = 'none';
    document.body.appendChild(a);
    a.click();
    document.body.removeChild(a);
  } finally {
    // Освобождаем URL — иначе blob висит в памяти до выгрузки страницы.
    // setTimeout, чтобы браузер успел инициировать загрузку.
    setTimeout(() => URL.revokeObjectURL(url), 1000);
  }
}
