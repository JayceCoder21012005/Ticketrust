import { defineConfig, devices } from "@playwright/test";

const port = 3100;

/** Runs against a production build (`npm run build` with ENABLE_DESIGN_PAGE=1 first). */
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
  webServer: {
    command: `npx next start -p ${port}`,
    url: `http://localhost:${port}`,
    reuseExistingServer: !process.env.CI,
    timeout: 60_000,
  },
});
