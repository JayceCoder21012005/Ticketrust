/**
 * Headers of the offline wallet's service worker (`public/sw.js`, spec 10). `no-cache` makes every update check reach
 * the server, so a fixed worker reaches users at once; `Service-Worker-Allowed: /` states the scope it registers with.
 */
export function serviceWorkerHeaders() {
  return [
    {
      source: "/sw.js",
      headers: [
        { key: "Content-Type", value: "application/javascript; charset=utf-8" },
        { key: "Cache-Control", value: "no-cache" },
        { key: "Service-Worker-Allowed", value: "/" },
      ],
    },
  ];
}
