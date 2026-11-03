const ORIGIN = "http://same-origin.invalid";
const AUTH_PAGES = new Set(["/login", "/register", "/forgot-password", "/reset-password", "/verify-email"]);

/**
 * `?returnUrl=` after login: only same-origin paths, so a crafted link can't bounce users to another site
 * (open redirect). Anything doubtful falls back to the home page.
 */
export function safeReturnUrl(value: string | null | undefined): string {
  // "//host" and "/\host" are protocol-relative URLs in browsers; control characters can hide either.
  if (!value || !value.startsWith("/") || /^\/[/\\]/.test(value) || /[\u0000-\u001f\\]/.test(value)) {
    return "/";
  }

  let url: URL;
  try {
    url = new URL(value, ORIGIN);
  } catch {
    return "/";
  }

  if (url.origin !== ORIGIN || decodeURIComponent(url.pathname).startsWith("//") || AUTH_PAGES.has(url.pathname)) {
    return "/";
  }

  return url.pathname + url.search + url.hash;
}
