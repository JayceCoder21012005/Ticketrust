/**
 * Organizer-entered links (venue map URL) end up in `href`. Only absolute http(s) URLs are rendered, so a
 * `javascript:` or `data:` value can never run in a visitor's browser.
 */
export function externalHttpUrl(value: string | null | undefined): string | null {
  if (!value) {
    return null;
  }

  try {
    const url = new URL(value.trim());
    return url.protocol === "https:" || url.protocol === "http:" ? url.toString() : null;
  } catch {
    return null;
  }
}
