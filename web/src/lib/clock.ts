/**
 * Device clocks drift and users change them, so countdowns run on server time (C10):
 * the api sends `serverNow` with every deadline and the client keeps the offset.
 */
export function clockOffsetMs(serverNow: string | Date, deviceNowAtReceipt: number): number {
  return new Date(serverNow).getTime() - deviceNowAtReceipt;
}

export function remainingMs(expiresAt: string | Date, deviceNow: number, offsetMs: number): number {
  return Math.max(0, new Date(expiresAt).getTime() - (deviceNow + offsetMs));
}

/** `mm:ss`, or `h:mm:ss` from one hour. Rounds up so "00:00" only appears when time is really up. */
export function formatCountdown(ms: number): string {
  const totalSeconds = Math.ceil(ms / 1000);
  const hours = Math.floor(totalSeconds / 3600);
  const minutes = Math.floor((totalSeconds % 3600) / 60);
  const seconds = totalSeconds % 60;
  const pad = (n: number) => n.toString().padStart(2, "0");

  return hours > 0 ? `${hours}:${pad(minutes)}:${pad(seconds)}` : `${pad(minutes)}:${pad(seconds)}`;
}
