import { existsSync, readFileSync } from "node:fs";
import { dirname, join, relative, resolve } from "node:path";
import { describe, expect, it } from "vitest";

const SRC = resolve(__dirname, "../..");
const PAGE = join(SRC, "app/(buyer)/tickets/[id]/page.tsx");

/** Value imports and re-exports (`import type` is erased by the compiler and never reaches the bundle). */
function staticSpecifiers(source: string): string[] {
  const statements = /^(?:import|export)\s+(?!type\s)(?:[^;'"]*?\sfrom\s+)?"([^"]+)"/gm;
  return [...source.matchAll(statements)].map((match) => match[1]!);
}

function resolveLocal(from: string, specifier: string): string | null {
  const base = specifier.startsWith("@/") ? join(SRC, specifier.slice(2)) : specifier.startsWith(".") ? resolve(dirname(from), specifier) : null;
  if (!base) return null;

  const candidates = [base, `${base}.ts`, `${base}.tsx`, join(base, "index.ts"), join(base, "index.tsx")];
  return candidates.find((file) => existsSync(file) && file.match(/\.tsx?$/)) ?? null;
}

/** Every source file the page pulls in through static imports. */
function staticGraph(entry: string): Set<string> {
  const seen = new Set<string>();
  const queue = [entry];
  while (queue.length > 0) {
    const file = queue.pop()!;
    if (seen.has(file)) continue;
    seen.add(file);
    for (const specifier of staticSpecifiers(readFileSync(file, "utf8"))) {
      const next = resolveLocal(file, specifier);
      if (next) queue.push(next);
    }
  }
  return seen;
}

describe("wallet page bundle", () => {
  it("QR_module_is_dynamically_imported", () => {
    const graph = [...staticGraph(PAGE)].map((file) => relative(SRC, file).replace(/\\/g, "/"));

    // Sanity: the walk does reach the wallet.
    expect(graph).toContain("features/tickets/wallet-view.tsx");
    expect(graph).toContain("features/tickets/rotating-qr.tsx");
    // The encoder (and its tables) only arrive through the dynamic import below.
    expect(graph.filter((file) => /lib\/tickets\/qr-(matrix|mask|tables|reed-solomon)\.ts$/.test(file))).toEqual([]);
    expect(readFileSync(join(SRC, "features/tickets/rotating-qr.tsx"), "utf8")).toMatch(/import\("@\/lib\/tickets\/qr-matrix"\)/);
  });

  it("the walk does see a static import of the encoder", () => {
    // Guards the guard: the regex catches the form a static import of the encoder would take.
    expect(staticSpecifiers('import { encodeQr } from "@/lib/tickets/qr-matrix";\nimport type { X } from "./y";')).toEqual(["@/lib/tickets/qr-matrix"]);
  });
});
