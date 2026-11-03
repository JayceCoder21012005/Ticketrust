/**
 * The access token lives in module memory only: never localStorage/sessionStorage (XSS-safe, spec 03).
 * The refresh token is an HttpOnly cookie the browser sends to `/api/v1/auth` automatically.
 */
let accessToken: string | null = null;

export function getAccessToken(): string | null {
  return accessToken;
}

export function setAccessToken(token: string | null): void {
  accessToken = token;
}

export async function refreshAccessToken(fetchImpl: typeof fetch = fetch): Promise<string | null> {
  const response = await fetchImpl("/api/v1/auth/refresh", {
    method: "POST",
    credentials: "same-origin",
    // Required by the api on refresh/logout as a CSRF guard (spec 03).
    headers: { "X-Requested-With": "fetch" },
  });

  if (!response.ok) {
    setAccessToken(null);
    return null;
  }

  const { accessToken: token } = (await response.json()) as { accessToken: string };
  setAccessToken(token);
  return token;
}

export function loginRedirectUrl(currentPathAndQuery: string): string {
  return `/login?returnUrl=${encodeURIComponent(currentPathAndQuery)}`;
}
