/**
 * The rotating ticket code (spec 10, format pinned in the design): `TR1.<ticketId b64url>.<step>.<sig b64url>`.
 * Byte for byte the same as the api's `QrCodeService`; `shared/fixtures/qr-vectors.json` keeps the two ports identical.
 * Pure and offline: Web Crypto only, so the code keeps rotating at a venue without signal.
 */
export const STEP_SECONDS = 15;
export const FORMAT = "TR1";

const SECRET_BYTES = 32;
/** First 10 bytes of the HMAC: 14 base64url chars. */
const SIGNATURE_BYTES = 10;
const GUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

function assertTime(value: number, name: string, integer: boolean) {
  if (!Number.isFinite(value) || value < 0 || (integer && !Number.isSafeInteger(value))) {
    throw new RangeError(`${name} must be a non-negative ${integer ? "integer" : "number"}.`);
  }
}

/** The 15-second window `unixSeconds` falls in. */
export function currentStep(unixSeconds: number): number {
  assertTime(unixSeconds, "unixSeconds", true);
  return Math.floor(unixSeconds / STEP_SECONDS);
}

/** Milliseconds until the next step starts (a full step on a boundary), for scheduling the next redraw. */
export function msUntilNextStep(nowMs: number): number {
  assertTime(nowMs, "nowMs", false);
  const period = STEP_SECONDS * 1000;
  return period - (nowMs % period);
}

/** Standard base64 (the `qr-key` secret) to bytes. */
export function base64ToBytes(base64: string): Uint8Array<ArrayBuffer> {
  const binary = atob(base64);
  return Uint8Array.from(binary, (char) => char.charCodeAt(0));
}

function toBase64Url(bytes: Uint8Array): string {
  const binary = Array.from(bytes, (byte) => String.fromCharCode(byte)).join("");
  return btoa(binary).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
}

/** The 16 bytes of a Guid in network (RFC 4122) order: the hex digits as written. */
function guidBytes(canonicalId: string): Uint8Array {
  const hex = canonicalId.replace(/-/g, "");
  return Uint8Array.from({ length: 16 }, (_, i) => parseInt(hex.slice(i * 2, i * 2 + 2), 16));
}

async function sign(secret: Uint8Array, message: string): Promise<Uint8Array> {
  const key = await crypto.subtle.importKey("raw", new Uint8Array(secret), { name: "HMAC", hash: "SHA-256" }, false, ["sign"]);
  const mac = await crypto.subtle.sign("HMAC", key, new TextEncoder().encode(message));
  return new Uint8Array(mac, 0, SIGNATURE_BYTES);
}

/** The code shown for `ticketId` during the step of `unixSeconds`. Throws `RangeError` on a malformed id, secret or time. */
export async function qrPayload(ticketId: string, secret: Uint8Array, unixSeconds: number): Promise<string> {
  if (!GUID.test(ticketId)) {
    throw new RangeError("ticketId must be a Guid (16 bytes).");
  }
  if (secret.length !== SECRET_BYTES) {
    throw new RangeError(`secret must be ${SECRET_BYTES} bytes.`);
  }

  const id = ticketId.toLowerCase();
  const step = currentStep(unixSeconds);
  const signature = await sign(secret, `${id}|${step}`);
  return `${FORMAT}.${toBase64Url(guidBytes(id))}.${step}.${toBase64Url(signature)}`;
}
