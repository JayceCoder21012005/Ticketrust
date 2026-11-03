/** Port of the api's `http` launch profile; used when the web app runs outside Aspire. */
export const DEFAULT_API_URL = "http://localhost:5080";

/**
 * The browser only ever talks to the web origin: `/api/*` and SignalR `/hubs/*` are proxied to the .NET api. This keeps the
 * refresh-token cookie first-party (SameSite=Strict, see spec 03) and removes the need for CORS.
 * Aspire injects `API_URL` in dev; Docker sets it at build time.
 */
export function apiRewrites(apiUrl: string | undefined) {
  const origin = (apiUrl || DEFAULT_API_URL).replace(/\/+$/, "");
  return [
    { source: "/api/:path*", destination: `${origin}/api/:path*` },
    { source: "/hubs/:path*", destination: `${origin}/hubs/:path*` },
  ];
}
