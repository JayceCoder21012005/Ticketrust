/**
 * The gateway address comes from the api, but it is still handed to `location.assign`: only an absolute http(s) URL
 * may go there (never `javascript:`, `data:` or a relative path).
 */
export function isSafeRedirect(value: string): boolean {
  try {
    const { protocol } = new URL(value);
    return protocol === "https:" || protocol === "http:";
  } catch {
    return false;
  }
}
