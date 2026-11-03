import createClient from "openapi-fetch";
import { createAuthFetch } from "./auth-fetch";
import type { paths } from "./schema";
import { getAccessToken, loginRedirectUrl, refreshAccessToken } from "./session";

const authFetch = createAuthFetch({
  baseFetch: (request) => fetch(request),
  getAccessToken,
  refresh: () => refreshAccessToken(),
  onUnauthenticated: () => {
    if (typeof window !== "undefined") {
      window.location.assign(loginRedirectUrl(window.location.pathname + window.location.search));
    }
  },
});

/**
 * Typed client generated from the api's OpenAPI document (`npm run api:gen`). Same-origin `/api` only;
 * the explicit origin keeps request URLs absolute, which `Request` needs outside the browser (tests).
 */
export const api = createClient<paths>({ baseUrl: globalThis.location?.origin ?? "", fetch: authFetch });
