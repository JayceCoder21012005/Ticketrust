/**
 * ISO/IEC 18004 tables for error-correction level M, versions 1–10: the only ones the ticket code needs
 * (a TR1 payload is ~50 bytes, version 4).
 */
export const MAX_VERSION = 10;

/** Codeword layout of one version: EC codewords per block, then the block groups as [count, data codewords per block]. */
export type BlockLayout = { ecPerBlock: number; groups: [number, number][] };

/** ISO 18004 table 9, level M. Index = version − 1. */
const LAYOUT_M: BlockLayout[] = [
  { ecPerBlock: 10, groups: [[1, 16]] },
  { ecPerBlock: 16, groups: [[1, 28]] },
  { ecPerBlock: 26, groups: [[1, 44]] },
  { ecPerBlock: 18, groups: [[2, 32]] },
  { ecPerBlock: 24, groups: [[2, 43]] },
  { ecPerBlock: 16, groups: [[4, 27]] },
  { ecPerBlock: 18, groups: [[4, 31]] },
  { ecPerBlock: 22, groups: [[2, 38], [2, 39]] },
  { ecPerBlock: 22, groups: [[3, 36], [2, 37]] },
  { ecPerBlock: 26, groups: [[4, 43], [1, 44]] },
];

/** ISO 18004 annex E: row/column centres of the alignment patterns. Index = version − 1. */
const ALIGNMENT_CENTRES: number[][] = [
  [],
  [6, 18],
  [6, 22],
  [6, 26],
  [6, 30],
  [6, 34],
  [6, 22, 38],
  [6, 24, 42],
  [6, 26, 46],
  [6, 28, 50],
];

export const blockLayout = (version: number): BlockLayout => LAYOUT_M[version - 1]!;

export const alignmentCentres = (version: number): number[] => ALIGNMENT_CENTRES[version - 1]!;

export const moduleCount = (version: number): number => 17 + 4 * version;

/** Data codewords of the whole symbol. */
export function dataCodewords(version: number): number {
  return blockLayout(version).groups.reduce((sum, [count, size]) => sum + count * size, 0);
}

/** Byte-mode character count indicator: 8 bits up to version 9, 16 from version 10. */
export const countBits = (version: number): number => (version <= 9 ? 8 : 16);

/** Mode indicator + count + data must fit the data codewords. */
export const fitsByteMode = (version: number, byteLength: number): boolean =>
  4 + countBits(version) + 8 * byteLength <= 8 * dataCodewords(version);
