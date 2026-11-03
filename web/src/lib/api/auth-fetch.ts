type AuthFetchOptions = {
  baseFetch: (request: Request) => Promise<Response>;
  /** Access token held in memory only (never localStorage, spec 03). */
  getAccessToken: () => string | null;
  /** Calls `/auth/refresh`; resolves to the new access token, or null when the session is gone. */
  refresh: () => Promise<string | null>;
  /** Session is over: send the user to login. */
  onUnauthenticated: () => void;
};

const REFRESH_PATH = "/api/v1/auth/refresh";

/**
 * fetch wrapper for the typed client: adds the bearer token and, on 401, refreshes silently once and
 * replays the request. Parallel 401s share one in-flight refresh so the rotating refresh token is
 * never presented twice (which spec 03 treats as theft).
 */
export function createAuthFetch({ baseFetch, getAccessToken, refresh, onUnauthenticated }: AuthFetchOptions) {
  let inFlight: Promise<string | null> | null = null;

  const refreshOnce = () => {
    inFlight ??= refresh()
      .catch(() => null)
      .finally(() => {
        inFlight = null;
      });
    return inFlight;
  };

  return async function authFetch(request: Request): Promise<Response> {
    const replay = request.clone();
    const response = await baseFetch(withToken(request, getAccessToken()));

    if (response.status !== 401 || new URL(request.url).pathname === REFRESH_PATH) {
      return response;
    }

    const token = await refreshOnce();
    if (!token) {
      onUnauthenticated();
      return response;
    }

    const retried = await baseFetch(withToken(replay, token));
    if (retried.status === 401) {
      onUnauthenticated();
    }
    return retried;
  };
}

function withToken(request: Request, token: string | null): Request {
  if (!token) {
    return request;
  }

  const headers = new Headers(request.headers);
  headers.set("Authorization", `Bearer ${token}`);
  return new Request(request, { headers });
}
