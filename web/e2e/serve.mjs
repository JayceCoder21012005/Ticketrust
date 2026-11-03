// Starts the mock api and `next start` together (Lighthouse CI takes a single start command).
// Usage: `node e2e/serve.mjs <web-port>`. Next's own "Ready" line is forwarded, which LHCI waits for.
import { spawn } from "node:child_process";
import { startMockApi } from "./mock-api.mjs";

const MOCK_API_PORT = 3199;
const webPort = process.argv[2] ?? "3200";

const api = await startMockApi(MOCK_API_PORT);
const next = spawn(process.execPath, ["node_modules/next/dist/bin/next", "start", "-p", webPort], {
  stdio: "inherit",
  env: { ...process.env, API_URL: `http://localhost:${MOCK_API_PORT}` },
});

// Never leave `next start` running when the runner stops us (it would hold the port and node_modules files).
const stop = () => {
  next.kill();
  api.close();
};
process.on("SIGINT", stop);
process.on("SIGTERM", stop);
process.on("exit", stop);
next.on("exit", (code) => {
  api.close();
  process.exit(code ?? 0);
});
