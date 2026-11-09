import { defineConfig, devices } from "@playwright/test";

const port = 3100;
/** The catalog pages read the api on the server: e2e runs against a mock (e2e/mock-api.mjs), never a real api. */
const mockApiPort = 3199;

/**
 * Runs against a production build (`npm run build` with ENABLE_DESIGN_PAGE=1 and API_URL=http://localhost:3199
 * first: the `/api` rewrite for browser calls is fixed at build time).
 *
 * Wall-clock `@perf` tests (e2e/organizer.spec.ts) assert only the stable budget by default; set PERF_BUDGET=strict on a
 * stable, otherwise idle runner (opt-in CI workflow perf-strict.yml) to also assert the cold timings: `PERF_BUDGET=strict npx playwright test --grep @perf`.
 */
export default defineConfig({
  testDir: "./e2e",
  fullyParallel: true,
  forbidOnly: !!process.env.CI,
  reporter: process.env.CI ? "github" : "list",
  use: { baseURL: `http://localhost:${port}` },
  projects: [
    { name: "chromium", use: { ...devices["Desktop Chrome"] }, testIgnore: /inp\.spec\.ts/ },
    // Latency measurements must not compete for CPU with other tests: run alone, one at a time, last.
    {
      name: "inp",
      use: { ...devices["Desktop Chrome"] },
      testMatch: /inp\.spec\.ts/,
      fullyParallel: false,
      dependencies: ["chromium"],
    },
  ],
  webServer: [
    {
      command: `node e2e/mock-api.mjs ${mockApiPort}`,
      url: `http://localhost:${mockApiPort}/api/v1/time`,
      reuseExistingServer: !process.env.CI,
    },
    {
      command: `npx next start -p ${port}`,
      url: `http://localhost:${port}`,
      // The mock gateway page is dev/demo only (404 in a production build); e2e opts in like a demo deployment.
      env: { API_URL: `http://localhost:${mockApiPort}`, ENABLE_MOCK_GATEWAY: "1" },
      reuseExistingServer: !process.env.CI,
      timeout: 60_000,
    },
  ],
});
