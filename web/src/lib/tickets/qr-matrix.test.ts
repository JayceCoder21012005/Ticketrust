import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { describe, expect, it } from "vitest";
import reference from "./fixtures/qr-reference.json";
import { encodeQr } from "./qr-matrix";
import { rsGenerator, rsRemainder, gfExp, gfMul } from "./qr-reed-solomon";

type Vector = { code: string };
const vectors = (JSON.parse(readFileSync(resolve(process.cwd(), "../shared/fixtures/qr-vectors.json"), "utf8")) as { cases: Vector[] }).cases;

/** Level M byte capacity of versions 1–10 (ISO/IEC 18004 table 7). */
const CAPACITY = [14, 26, 42, 62, 84, 106, 122, 152, 180, 213];

const versionOf = (matrix: boolean[][]) => (matrix.length - 17) / 4;
const text = (length: number) => "x".repeat(length);
const toRows = (matrix: boolean[][]) => matrix.map((row) => row.map((dark) => (dark ? "1" : "0")).join(""));

/** Remainder of `value` (as a GF(2) polynomial) divided by `generator`. */
function bchRemainder(value: number, generator: number): number {
  const degree = Math.floor(Math.log2(generator));
  let rem = value;
  for (let bit = Math.floor(Math.log2(Math.max(rem, 1))); bit >= degree; bit--) {
    if ((rem >> bit) & 1) {
      rem ^= generator << (bit - degree);
    }
  }

  return rem;
}

/** The 15 format bits of the copy around the top-left finder, most significant first (ISO 18004 figure 25). */
function readFormatTopLeft(m: boolean[][]): number {
  const cells: [number, number][] = [];
  for (let x = 0; x <= 5; x++) cells.push([8, x]);
  cells.push([8, 7], [8, 8], [7, 8]);
  for (let y = 5; y >= 0; y--) cells.push([y, 8]);

  return cells.reduce((bits, [y, x]) => (bits << 1) | (m[y]![x] ? 1 : 0), 0);
}

/** The second copy: down the column under the top-right finder, then along the row right of the bottom-left finder. */
function readFormatSecondCopy(m: boolean[][]): number {
  const size = m.length;
  const cells: [number, number][] = [];
  for (let y = size - 1; y >= size - 7; y--) cells.push([y, 8]);
  for (let x = size - 8; x < size; x++) cells.push([8, x]);

  return cells.reduce((bits, [y, x]) => (bits << 1) | (m[y]![x] ? 1 : 0), 0);
}

/** 18 version bits from the block above the bottom-left finder, most significant first. */
function readVersionInfo(m: boolean[][], transposed: boolean): number {
  const size = m.length;
  let bits = 0;
  for (let i = 17; i >= 0; i--) {
    const a = size - 11 + (i % 3);
    const b = Math.floor(i / 3);
    const dark = transposed ? m[b]![a]! : m[a]![b]!;
    bits = (bits << 1) | (dark ? 1 : 0);
  }

  return bits;
}

function expectFinder(m: boolean[][], top: number, left: number) {
  for (let dy = -1; dy <= 7; dy++) {
    for (let dx = -1; dx <= 7; dx++) {
      const y = top + dy;
      const x = left + dx;
      if (y < 0 || x < 0 || y >= m.length || x >= m.length) continue;
      const ring = Math.max(Math.abs(dy - 3), Math.abs(dx - 3));
      expect(m[y]![x], `finder at ${top},${left} module ${y},${x}`).toBe(ring !== 2 && ring !== 4);
    }
  }
}

describe("encodeQr", () => {
  it("Matches_reference_matrices", () => {
    expect(reference.length).toBeGreaterThanOrEqual(12);
    for (const entry of reference) {
      expect(toRows(encodeQr(entry.payload)), entry.payload).toEqual(entry.rows);
    }
  });

  it("Version_grows_with_payload_length_up_to_the_limit", () => {
    CAPACITY.forEach((capacity, index) => {
      const version = index + 1;
      expect(encodeQr(text(capacity)).length, `capacity of v${version}`).toBe(17 + 4 * version);
      if (version < 10) {
        expect(encodeQr(text(capacity + 1)).length, `capacity+1 of v${version}`).toBe(17 + 4 * (version + 1));
      }
    });
    expect(encodeQr("").length).toBe(21);
  });

  it("counts UTF-8 bytes, not characters", () => {
    expect(encodeQr("đ".repeat(7)).length).toBe(21);
    expect(encodeQr("đ".repeat(8)).length).toBe(25);
  });

  it("Rejects_payloads_longer_than_version_10_M_capacity", () => {
    expect(encodeQr(text(213)).length).toBe(57);
    expect(() => encodeQr(text(214))).toThrow(RangeError);
    expect(() => encodeQr("ệ".repeat(72))).toThrow(RangeError);
  });

  it("Finder_timing_and_format_patterns_are_valid", () => {
    for (const length of [10, 30, 50, 70, 100, 120, 150, 170, 200, 213]) {
      const m = encodeQr(text(length));
      const size = m.length;
      const version = versionOf(m);

      expectFinder(m, 0, 0);
      expectFinder(m, 0, size - 7);
      expectFinder(m, size - 7, 0);
      for (let i = 8; i < size - 8; i++) {
        expect(m[6]![i], `timing row ${i}`).toBe(i % 2 === 0);
        expect(m[i]![6], `timing column ${i}`).toBe(i % 2 === 0);
      }
      expect(m[size - 8]![8], "dark module").toBe(true);

      const format = readFormatTopLeft(m);
      expect(readFormatSecondCopy(m)).toBe(format);
      const unmasked = format ^ 0x5412;
      expect(bchRemainder(unmasked, 0x537), "format BCH").toBe(0);
      expect(unmasked >> 13, "level M").toBe(0b00);

      if (version >= 7) {
        const info = readVersionInfo(m, false);
        expect(readVersionInfo(m, true)).toBe(info);
        expect(info >> 12).toBe(version);
        expect(bchRemainder(info, 0x1f25), "version BCH").toBe(0);
      }
    }
  });

  it("puts version 7 information at its ISO value", () => {
    expect(readVersionInfo(encodeQr(text(110)), false)).toBe(0x07c94);
  });

  it("draws alignment patterns at the ISO centres", () => {
    const centres: Record<number, number[]> = { 2: [6, 18], 6: [6, 34], 7: [6, 22, 38], 10: [6, 28, 50] };
    const lengthFor: Record<number, number> = { 2: 20, 6: 100, 7: 110, 10: 200 };

    for (const [version, positions] of Object.entries(centres)) {
      const m = encodeQr(text(lengthFor[Number(version)]!));
      const last = positions.length - 1;
      positions.forEach((cy, i) =>
        positions.forEach((cx, j) => {
          if ((i === 0 && j === 0) || (i === 0 && j === last) || (i === last && j === 0)) return;
          for (let dy = -2; dy <= 2; dy++) {
            for (let dx = -2; dx <= 2; dx++) {
              expect(m[cy + dy]![cx + dx], `v${version} alignment ${cy},${cx}`).toBe(Math.max(Math.abs(dx), Math.abs(dy)) !== 1);
            }
          }
        }),
      );
    }
  });

  it("Payloads_of_real_TR1_length_encode_to_a_stable_size", () => {
    const sizes = new Set(vectors.slice(0, 200).map((v) => encodeQr(v.code).length));
    const lengths = vectors.map((v) => v.code.length);

    expect(Math.max(...lengths)).toBeLessThanOrEqual(62);
    expect([...sizes]).toEqual([33]);
  });

  it("Deterministic_output", () => {
    const payload = vectors[0]!.code;
    expect(toRows(encodeQr(payload))).toEqual(toRows(encodeQr(payload)));
  });
});

describe("Reed–Solomon over GF(256)", () => {
  it("Reed_Solomon_matches_known_vectors", () => {
    // Thonky's "HELLO WORLD" (alphanumeric) examples, versions 1-M and 1-Q.
    const data1M = [32, 91, 11, 120, 209, 114, 220, 77, 67, 64, 236, 17, 236, 17, 236, 17];
    expect(rsRemainder(data1M, 10)).toEqual([196, 35, 39, 119, 235, 215, 231, 226, 93, 23]);

    const data1Q = [32, 91, 11, 120, 209, 114, 220, 77, 67, 64, 236, 17, 236];
    expect(rsRemainder(data1Q, 13)).toEqual([168, 72, 22, 82, 217, 54, 156, 0, 46, 15, 180, 122, 16]);
  });

  it("builds the published degree-10 generator", () => {
    const log = (value: number) => Array.from({ length: 255 }, (_, i) => i).find((i) => gfExp(i) === value);
    expect(rsGenerator(10).map(log)).toEqual([0, 251, 67, 46, 61, 118, 70, 64, 94, 32, 45]);
  });

  it("produces codewords divisible by the generator (zero syndromes)", () => {
    for (const degree of [10, 16, 18, 22, 26]) {
      const data = Array.from({ length: 40 }, (_, i) => (i * 37 + degree) & 0xff);
      const codeword = [...data, ...rsRemainder(data, degree)];
      for (let root = 0; root < degree; root++) {
        const syndrome = codeword.reduce((acc, coefficient) => gfMul(acc, gfExp(root)) ^ coefficient, 0);
        expect(syndrome, `degree ${degree} root ${root}`).toBe(0);
      }
    }
  });
});
