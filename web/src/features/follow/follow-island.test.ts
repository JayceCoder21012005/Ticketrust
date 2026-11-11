import { readFileSync } from "node:fs";
import { join } from "node:path";
import { describe, expect, it } from "vitest";

const read = (file: string) => readFileSync(join(__dirname, file), "utf8");
const staticImports = (source: string) => [...source.matchAll(/^import\s[^;]*?from\s+"([^"]+)"/gm)].map((match) => match[1]!);

/**
 * The public event page has a JS budget (170 KB): an anonymous visitor's button must not drag react-query, the api client
 * or the follow hook with it. Only the lazily imported control may.
 */
describe("follow button island", () => {
  const heavy = (specifier: string) => /react-query|^@\/lib\/api|^@\/lib\/follow|use-follow|follow-control|sonner|signalr/.test(specifier);

  it.each(["follow-button.tsx", "follow-ui.tsx"])("%s statically imports nothing that pulls in react-query", (file) => {
    expect(staticImports(read(file)).filter(heavy)).toEqual([]);
  });

  it("loads the control only through a dynamic import", () => {
    expect(read("follow-button.tsx")).toMatch(/lazy\(\(\) => import\("\.\/follow-control"\)\)/);
  });
});
