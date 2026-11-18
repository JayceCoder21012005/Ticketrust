/**
 * A code stays readable for up to 15 s, and the camera reads it many times a second. Once a code has been sent, the same code
 * is ignored for as long as the camera keeps seeing it, and for `quietMs` after it was last seen. A different code (another
 * ticket, or the next rotation) goes through at once. The server shields double reads too (3 s); this keeps the screen calm.
 */
export function createRepeatFilter(quietMs = 1500) {
  let last: string | null = null;
  let lastSeenAt = 0;

  return {
    /** True when this reading should be sent. */
    accept(payload: string, now: number): boolean {
      const repeated = payload === last && now - lastSeenAt < quietMs;
      last = payload;
      lastSeenAt = now;
      return !repeated;
    },
    /** The last code counts as seen right now (the scanner was paused and the ticket may still be in front of the camera). */
    touch(now: number) {
      lastSeenAt = now;
    },
    reset() {
      last = null;
    },
  };
}
