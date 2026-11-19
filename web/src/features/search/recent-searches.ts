const KEY = "tr-recent-searches";
const MAX = 5;

function read(): string[] {
  try {
    const parsed: unknown = JSON.parse(localStorage.getItem(KEY) ?? "[]");
    return Array.isArray(parsed) ? parsed.filter((v): v is string => typeof v === "string").slice(0, MAX) : [];
  } catch {
    return [];
  }
}

function write(values: string[]): void {
  try {
    if (values.length === 0) localStorage.removeItem(KEY);
    else localStorage.setItem(KEY, JSON.stringify(values));
  } catch {
    // Private mode or blocked storage: the palette works without history.
  }
}

/** The last five searches of this browser (a per-viewer convenience; nothing breaks without it). */
export const recentSearches = {
  get: read,
  add(query: string): void {
    const q = query.trim();
    if (!q) return;
    write([q, ...read().filter((v) => v !== q)].slice(0, MAX));
  },
  clear(): void {
    write([]);
  },
};
