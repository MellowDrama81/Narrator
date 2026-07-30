export function downloadJson(filename: string, value: unknown): void {
  download(filename, JSON.stringify(value, null, 2), 'application/json');
}

export function downloadText(filename: string, value: string): void {
  download(filename, value, 'text/plain;charset=utf-8');
}

function download(filename: string, value: string, type: string): void {
  const url = URL.createObjectURL(new Blob([value], { type }));
  const anchor = document.createElement('a');
  anchor.href = url;
  anchor.download = filename;
  anchor.click();
  URL.revokeObjectURL(url);
}

export function safeFilename(value: string): string {
  return value.replace(/[<>:"/\\|?*\u0000-\u001f]/g, '-').trim() || 'mellow-narrator';
}

