// `next build` with the mock api running, so the home page and the soonest event pages are prerendered with
// data, as they would be against a real api. Used by CI before e2e and Lighthouse (`npm run build:e2e`).
import { spawn } from "node:child_process";
import { startMockApi } from "./mock-api.mjs";

const MOCK_API_PORT = 3199;
const api = await startMockApi(MOCK_API_PORT);
const build = spawn(process.execPath, ["node_modules/next/dist/bin/next", "build"], {
  stdio: "inherit",
  env: { ...process.env, API_URL: `http://localhost:${MOCK_API_PORT}` },
});

build.on("exit", (code) => {
  api.close();
  process.exit(code ?? 1);
});
