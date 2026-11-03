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

/** Typed client generated from the api's OpenAPI document (`npm run api:gen`). Same-origin `/api` only. */
export const api = createClient<paths>({ baseUrl: "", fetch: authFetch });
