/**
 * A small ISO/IEC 18004 QR Code encoder: byte mode, error-correction level M, versions 1–10. In-repo because adding a
 * dependency is not allowed (lockfile rule, spec 10); verified against zxing-cpp and pinned by `fixtures/qr-reference.json`.
 * The matrix excludes the quiet zone: callers draw 4 light modules around it. `true` = dark.
 */
import { applyMask, MASK_COUNT, penalty, type Grid } from "./qr-mask";
import { rsRemainder } from "./qr-reed-solomon";
import { alignmentCentres, blockLayout, countBits, dataCodewords, fitsByteMode, MAX_VERSION, moduleCount } from "./qr-tables";

const BYTE_MODE = 0b0100;
const PAD_BYTES = [0xec, 0x11];
/** Error-correction level M is `00` in the format information. */
const LEVEL_M_BITS = 0b00;

type QrSymbol = { size: number; modules: Grid; isFunction: Grid };

/** The smallest version whose level-M data capacity holds `byteLength` bytes. */
function chooseVersion(byteLength: number): number {
  for (let version = 1; version <= MAX_VERSION; version++) {
    if (fitsByteMode(version, byteLength)) {
      return version;
    }
  }

  throw new RangeError(`QR payload of ${byteLength} bytes exceeds the version ${MAX_VERSION}-M capacity.`);
}

/** Mode, count, data, terminator, bit padding and pad bytes: exactly the version's data codewords. */
function dataStream(bytes: Uint8Array, version: number): number[] {
  const capacityBits = dataCodewords(version) * 8;
  const bits: number[] = [];
  const push = (value: number, length: number) => {
    for (let i = length - 1; i >= 0; i--) bits.push((value >>> i) & 1);
  };

  push(BYTE_MODE, 4);
  push(bytes.length, countBits(version));
  bytes.forEach((byte) => push(byte, 8));
  push(0, Math.min(4, capacityBits - bits.length));
  push(0, (8 - (bits.length % 8)) % 8);

  const codewords = Array.from({ length: bits.length / 8 }, (_, i) => bits.slice(i * 8, i * 8 + 8).reduce((acc, bit) => (acc << 1) | bit, 0));
  for (let i = 0; codewords.length < capacityBits / 8; i++) {
    codewords.push(PAD_BYTES[i % 2]!);
  }

  return codewords;
}

/** Splits the data into blocks, adds each block's EC codewords and interleaves both (ISO 18004 7.6). */
function interleave(data: number[], version: number): number[] {
  const { ecPerBlock, groups } = blockLayout(version);
  const blocks: number[][] = [];
  let offset = 0;
  for (const [count, size] of groups) {
    for (let b = 0; b < count; b++, offset += size) {
      blocks.push(data.slice(offset, offset + size));
    }
  }

  const ec = blocks.map((block) => rsRemainder(block, ecPerBlock));
  const longest = Math.max(...blocks.map((block) => block.length));
  const result: number[] = [];
  for (let i = 0; i < longest; i++) {
    // Shorter blocks come first and simply run out one codeword early.
    blocks.filter((block) => i < block.length).forEach((block) => result.push(block[i]!));
  }
  for (let i = 0; i < ecPerBlock; i++) {
    ec.forEach((block) => result.push(block[i]!));
  }
  return result;
}

function emptySymbol(version: number): QrSymbol {
  const size = moduleCount(version);
  const grid = () => Array.from({ length: size }, () => new Array<boolean>(size).fill(false));
  return { size, modules: grid(), isFunction: grid() };
}

function setFunction(symbol: QrSymbol, row: number, col: number, dark: boolean) {
  symbol.modules[row]![col] = dark;
  symbol.isFunction[row]![col] = true;
}

/** A finder pattern centred at (row, col) with its light separator (clipped at the symbol's edge). */
function drawFinder(symbol: QrSymbol, row: number, col: number) {
  for (let dy = -4; dy <= 4; dy++) {
    for (let dx = -4; dx <= 4; dx++) {
      const r = row + dy;
      const c = col + dx;
      const ring = Math.max(Math.abs(dy), Math.abs(dx));
      if (r >= 0 && r < symbol.size && c >= 0 && c < symbol.size) {
        setFunction(symbol, r, c, ring !== 2 && ring !== 4);
      }
    }
  }
}

function drawAlignment(symbol: QrSymbol, row: number, col: number) {
  for (let dy = -2; dy <= 2; dy++) {
    for (let dx = -2; dx <= 2; dx++) {
      setFunction(symbol, row + dy, col + dx, Math.max(Math.abs(dy), Math.abs(dx)) !== 1);
    }
  }
}

/** Alignment patterns at every pair of centres except the three that overlap a finder. */
function drawAlignments(symbol: QrSymbol, version: number) {
  const centres = alignmentCentres(version);
  const last = centres.length - 1;
  centres.forEach((row, i) =>
    centres.forEach((col, j) => {
      const onFinder = (i === 0 && j === 0) || (i === 0 && j === last) || (i === last && j === 0);
      if (!onFinder) drawAlignment(symbol, row, col);
    }),
  );
}

function drawFunctionPatterns(symbol: QrSymbol, version: number) {
  for (let i = 0; i < symbol.size; i++) {
    setFunction(symbol, 6, i, i % 2 === 0);
    setFunction(symbol, i, 6, i % 2 === 0);
  }
  drawFinder(symbol, 3, 3);
  drawFinder(symbol, 3, symbol.size - 4);
  drawFinder(symbol, symbol.size - 4, 3);
  drawAlignments(symbol, version);
  drawFormatBits(symbol, 0);
  drawVersionBits(symbol, version);
}

/** BCH(15,5) format information for level M and `mask`, XOR-ed with 0x5412. */
function formatBits(mask: number): number {
  const data = (LEVEL_M_BITS << 3) | mask;
  let rem = data;
  for (let i = 0; i < 10; i++) rem = (rem << 1) ^ ((rem >>> 9) * 0x537);
  return ((data << 10) | rem) ^ 0x5412;
}

/** Both copies of the format information, plus the dark module (ISO 18004 7.9.1). */
function drawFormatBits(symbol: QrSymbol, mask: number) {
  const bits = formatBits(mask);
  const bit = (i: number) => ((bits >>> i) & 1) === 1;
  const size = symbol.size;

  for (let i = 0; i <= 5; i++) setFunction(symbol, i, 8, bit(i));
  setFunction(symbol, 7, 8, bit(6));
  setFunction(symbol, 8, 8, bit(7));
  setFunction(symbol, 8, 7, bit(8));
  for (let i = 9; i < 15; i++) setFunction(symbol, 8, 14 - i, bit(i));

  for (let i = 0; i < 8; i++) setFunction(symbol, 8, size - 1 - i, bit(i));
  for (let i = 8; i < 15; i++) setFunction(symbol, size - 15 + i, 8, bit(i));
  setFunction(symbol, size - 8, 8, true);
}

/** BCH(18,6) version information in the two 6×3 blocks, from version 7 (ISO 18004 7.10). */
function drawVersionBits(symbol: QrSymbol, version: number) {
  if (version < 7) return;
  let rem = version;
  for (let i = 0; i < 12; i++) rem = (rem << 1) ^ ((rem >>> 11) * 0x1f25);
  const bits = (version << 12) | rem;

  for (let i = 0; i < 18; i++) {
    const dark = ((bits >>> i) & 1) === 1;
    const a = symbol.size - 11 + (i % 3);
    const b = Math.floor(i / 3);
    setFunction(symbol, a, b, dark);
    setFunction(symbol, b, a, dark);
  }
}

/** Zigzag placement: two-module columns from the right, alternating up and down, skipping the timing column. */
function placeCodewords(symbol: QrSymbol, codewords: number[]) {
  const totalBits = codewords.length * 8;
  let index = 0;
  for (let right = symbol.size - 1; right >= 1; right -= 2) {
    if (right === 6) right = 5;
    const upward = ((right + 1) & 2) === 0;
    for (let step = 0; step < symbol.size; step++) {
      const row = upward ? symbol.size - 1 - step : step;
      for (let c = right; c >= right - 1; c--) {
        if (symbol.isFunction[row]![c] || index >= totalBits) continue;
        symbol.modules[row]![c] = ((codewords[index >>> 3]! >>> (7 - (index & 7))) & 1) === 1;
        index++;
      }
    }
  }
}

/** Tries all eight masks (format information included) and keeps the lowest penalty; ties go to the lowest index. */
function bestMasked(symbol: QrSymbol): Grid {
  let best: Grid = symbol.modules;
  let bestScore = Number.POSITIVE_INFINITY;
  for (let mask = 0; mask < MASK_COUNT; mask++) {
    drawFormatBits(symbol, mask);
    const candidate = applyMask(symbol.modules, symbol.isFunction, mask);
    const score = penalty(candidate);
    if (score < bestScore) {
      best = candidate;
      bestScore = score;
    }
  }

  return best;
}

/** The module matrix of `text` (UTF-8, byte mode, level M). Throws `RangeError` beyond 213 bytes (version 10-M). */
export function encodeQr(text: string): boolean[][] {
  const bytes = new TextEncoder().encode(text);
  const version = chooseVersion(bytes.length);
  const symbol = emptySymbol(version);
  drawFunctionPatterns(symbol, version);
  placeCodewords(symbol, interleave(dataStream(bytes, version), version));
  return bestMasked(symbol);
}
