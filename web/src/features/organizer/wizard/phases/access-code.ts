/** No 0/O or 1/I: codes are read out on stage and typed on phones. */
const ALPHABET = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

/** 8 random characters (32 symbols, so the modulo below is unbiased). */
export function generateAccessCode(random: (array: Uint8Array) => Uint8Array = (a) => crypto.getRandomValues(a)): string {
  return Array.from(random(new Uint8Array(8)), (byte) => ALPHABET[byte % ALPHABET.length]).join("");
}
