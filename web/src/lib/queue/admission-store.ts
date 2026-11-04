const KEY = "tr.admission";

/** `storage` events fire only in other tabs; this one tells the timer of the tab that changed it. */
export const ADMISSION_CHANGED_EVENT = "tr:admission";

const announce = () => window.dispatchEvent(new Event(ADMISSION_CHANGED_EVENT));

export type StoredAdmission = {
  eventId: string;
  slug: string;
  token: string;
  /** ISO instant at which the shopping window ends (server time). */
  expiresAt: string;
};

/**
 * The latest admission, shared by every tab of this browser (localStorage), so the seat page, a second tab and the
 * global shopping timer all see the same window. The token is bound to the signed-in user and useless without
 * their session, which is why it may live in storage while the access token may not (spec 03).
 * Every access is guarded: storage can be blocked or full.
 */
export function saveAdmission(admission: StoredAdmission): void {
  try {
    localStorage.setItem(KEY, JSON.stringify(admission));
    announce();
  } catch {
    // Without storage the timer simply stays hidden; the queue page keeps working.
  }
}

export function loadAdmission(now: number = Date.now()): StoredAdmission | null {
  try {
    const raw = localStorage.getItem(KEY);
    if (!raw) {
      return null;
    }

    const value = JSON.parse(raw) as Partial<StoredAdmission>;
    const valid =
      typeof value.eventId === "string" &&
      typeof value.slug === "string" &&
      typeof value.token === "string" &&
      typeof value.expiresAt === "string" &&
      Date.parse(value.expiresAt) > now;
    return valid ? (value as StoredAdmission) : null;
  } catch {
    return null;
  }
}

export function clearAdmission(eventId: string): void {
  try {
    if (loadAdmission()?.eventId === eventId) {
      localStorage.removeItem(KEY);
      announce();
    }
  } catch {
    // Nothing to clean up if storage is unavailable.
  }
}
