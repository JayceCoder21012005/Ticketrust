// CI gate for build/test tooling: no high or critical advisory, except the ones listed below.
// Shipped code is checked separately and strictly (`npm audit --omit=dev --audit-level=low`).
import { execSync } from "node:child_process";

// Each exception names the advisory, why it is safe to wait, and the day it stops being accepted.
const ACCEPTED = [
  {
    id: "GHSA-fc5q-39xc-vjpf",
    why: "picomatch ReDoS (published 2026-10-19): no patched version exists yet; reached only through vitest, shadcn and eslint-config-next (dev tooling, never shipped, input is our own file names)",
    until: "2026-12-31",
  },
];

let raw;
try {
  raw = execSync("npm audit --json", { encoding: "utf8", stdio: ["ignore", "pipe", "ignore"] });
} catch (error) {
  raw = error.stdout; // npm audit exits non-zero when it finds something
}

const today = new Date().toISOString().slice(0, 10);
const active = new Set(ACCEPTED.filter((a) => a.until >= today).map((a) => a.id));
for (const a of ACCEPTED.filter((x) => x.until < today)) console.error(`Accepted advisory ${a.id} expired on ${a.until}: ${a.why}`);

const { vulnerabilities = {} } = JSON.parse(raw);
const failures = new Map();
for (const [name, vuln] of Object.entries(vulnerabilities)) {
  if (vuln.severity !== "high" && vuln.severity !== "critical") continue;
  const direct = vuln.via.filter((v) => typeof v === "object");
  // A package that only inherits another package's advisory is covered when that advisory is accepted.
  const open = direct.filter((v) => !active.has(v.url.split("/").pop()));
  if (direct.length === 0 || open.length > 0) failures.set(name, open.map((v) => v.url));
}
// Packages that only "depend on a vulnerable package" are reported through their via-chain; judge only the roots.
const roots = [...failures].filter(([, urls]) => urls.length > 0);
if (roots.length > 0) {
  for (const [name, urls] of roots) console.error(`${name}: ${urls.join(" ")}`);
  process.exit(1);
}
console.log("Tooling audit: no unaccepted high or critical advisory.");
