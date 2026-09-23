// Saves a blob the browser has in memory (e.g. the diagnostics zip) through the normal download
// path, so a user ends up with a real file they can attach to an email.
export function saveBlob(blob: Blob, fileName: string): void {
  const url = URL.createObjectURL(blob);
  const link = document.createElement("a");
  link.href = url;
  link.download = fileName;
  document.body.appendChild(link);
  link.click();
  link.remove();
  URL.revokeObjectURL(url);
}
