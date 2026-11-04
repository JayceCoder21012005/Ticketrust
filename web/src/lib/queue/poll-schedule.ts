const MIN_DELAY_MS = 500;
const MAX_DELAY_MS = 300_000;
const MAX_BACKOFF_MS = 30_000;

/**
 * Delay before the next poll. With a response the server decides (it knows the crowd size), clamped so a bad value
 * can neither hammer the api nor park the client for good. After failures: exponential backoff from 1 s up to 30 s,
 * with +-20% jitter so a recovering api is not hit by every client at the same instant.
 */
export function nextDelayMs(response: { pollAfterMs: number } | null, failures: number, random: () => number = Math.random): number {
  if (response) {
    return Math.min(MAX_DELAY_MS, Math.max(MIN_DELAY_MS, response.pollAfterMs));
  }

  const backoff = Math.min(MAX_BACKOFF_MS, 1_000 * 2 ** Math.max(0, failures - 1));
  return Math.min(MAX_BACKOFF_MS, Math.round(backoff * (0.8 + 0.4 * random())));
}
