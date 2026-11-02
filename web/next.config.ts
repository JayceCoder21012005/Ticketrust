import type { NextConfig } from "next";
import { apiRewrites } from "./src/lib/api-rewrites";

const nextConfig: NextConfig = {
  output: "standalone",
  cacheComponents: true,
  partialPrefetching: true,
  poweredByHeader: false,
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
};

export default nextConfig;
