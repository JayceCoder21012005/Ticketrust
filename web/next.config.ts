import type { NextConfig } from "next";
import { apiRewrites } from "./src/lib/api-rewrites";
import { serviceWorkerHeaders } from "./src/lib/service-worker-headers";

/** Covers, avatars and artwork are served by the storage host (Storage:PublicBaseUrl on the api). */
const storageUrl = new URL(process.env.STORAGE_PUBLIC_URL ?? "http://localhost:3000/media");

const nextConfig: NextConfig = {
  output: "standalone",
  reactCompiler: true,
  cacheComponents: true,
  partialPrefetching: true,
  poweredByHeader: false,
  images: {
    remotePatterns: [
      {
        protocol: storageUrl.protocol.replace(":", "") as "http" | "https",
        hostname: storageUrl.hostname,
        port: storageUrl.port,
        pathname: `${storageUrl.pathname.replace(/\/$/, "")}/**`,
      },
    ],
  },
  turbopack: {
    rules: {
      "*.css": {
        loaders: ["@tailwindcss/turbopack"],
        as: "*.css",
      },
    },
  },
  async rewrites() {
    return apiRewrites(process.env.API_URL);
  },
  async headers() {
    return serviceWorkerHeaders();
  },
};

export default nextConfig;
