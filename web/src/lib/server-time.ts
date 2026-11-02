export type ServerTime = { serverNow: Date };

/** Reads the api clock through the same-origin `/api` rewrite (used for clock-offset, C10). */
export async function fetchServerTime(signal?: AbortSignal): Promise<ServerTime> {
  const response = await fetch("/api/v1/time", { cache: "no-store", signal });
  if (!response.ok) {
    throw new Error(`GET /api/v1/time failed with ${response.status}`);
  }

  const body = (await response.json()) as { serverNow: string };
  return { serverNow: new Date(body.serverNow) };
}
