/** Reed–Solomon error correction over GF(256) with the QR primitive polynomial x⁸+x⁴+x³+x²+1 (0x11D). */
const PRIMITIVE = 0x11d;

const EXP = new Uint8Array(512);
const LOG = new Uint8Array(256);

(function buildTables() {
  let value = 1;
  for (let power = 0; power < 255; power++) {
    EXP[power] = value;
    LOG[value] = power;
    value <<= 1;
    if (value & 0x100) {
      value ^= PRIMITIVE;
    }
  }
  // Doubled, so a product's exponent never needs a modulo.
  for (let power = 255; power < 512; power++) {
    EXP[power] = EXP[power - 255]!;
  }
})();

/** α^power. */
export const gfExp = (power: number): number => EXP[power % 255]!;

export function gfMul(a: number, b: number): number {
  return a === 0 || b === 0 ? 0 : EXP[LOG[a]! + LOG[b]!]!;
}

/** Generator polynomial ∏(x − αⁱ), i = 0…degree−1; coefficients from the highest power down (monic: the first is 1). */
export function rsGenerator(degree: number): number[] {
  let poly = [1];
  for (let i = 0; i < degree; i++) {
    const root = gfExp(i);
    const next = [...poly, 0];
    for (let j = 1; j < next.length; j++) {
      next[j] = next[j]! ^ gfMul(poly[j - 1]!, root);
    }
    poly = next;
  }

  return poly;
}

/** The `degree` EC codewords of `data`: the remainder of data·x^degree divided by the generator. */
export function rsRemainder(data: number[], degree: number): number[] {
  const generator = rsGenerator(degree);
  const remainder = new Array<number>(degree).fill(0);
  for (const byte of data) {
    const factor = byte ^ remainder.shift()!;
    remainder.push(0);
    for (let i = 0; i < degree; i++) {
      remainder[i] = remainder[i]! ^ gfMul(generator[i + 1]!, factor);
    }
  }

  return remainder;
}
