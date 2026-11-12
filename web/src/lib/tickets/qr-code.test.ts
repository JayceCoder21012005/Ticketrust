import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { webcrypto } from "node:crypto";
import { afterEach, beforeAll, describe, expect, it, vi } from "vitest";
import { base64ToBytes, currentStep, FORMAT, msUntilNextStep, qrPayload, STEP_SECONDS } from "./qr-code";

type Vector = { ticketId: string; secret: string; unixSeconds: number; code: string };
type Vectors = { seed: number; stepSeconds: number; format: string; cases: Vector[] };

const vectors = JSON.parse(readFileSync(resolve(process.cwd(), "../shared/fixtures/qr-vectors.json"), "utf8")) as Vectors;

const ticketId = "0192f0e0-aaaa-7bbb-8ccc-0123456789ab";
const secret = new Uint8Array(32).fill(7);

beforeAll(() => {
  // jsdom has no SubtleCrypto; the browser and Node both do, so the test borrows Node's Web Crypto.
  if (!globalThis.crypto?.subtle) {
    vi.stubGlobal("crypto", webcrypto);
  }
});

afterEach(() => vi.unstubAllGlobals());

describe("qrPayload", () => {
  it("Generator_matches_all_1000_shared_vectors", async () => {
    expect(vectors.cases).toHaveLength(1000);
    expect(vectors.stepSeconds).toBe(STEP_SECONDS);
    expect(vectors.format).toBe(FORMAT);

    for (const vector of vectors.cases) {
      const code = await qrPayload(vector.ticketId, base64ToBytes(vector.secret), vector.unixSeconds);
      expect(code, vector.ticketId).toBe(vector.code);
    }
  });

  it("formats the id as 22 base64url chars, the step in decimal and a 14-char signature", async () => {
    const code = await qrPayload(ticketId, secret, 1_800_000_000);

    expect(code).toMatch(/^TR1\.AZLw4Kqqe7uMzAEjRWeJqw\.120000000\.[A-Za-z0-9_-]{14}$/);
  });

  it("accepts an upper-case id and signs its lower-case form", async () => {
    expect(await qrPayload(ticketId.toUpperCase(), secret, 30)).toBe(await qrPayload(ticketId, secret, 30));
  });

  it("Generator_throws_on_a_non_16_byte_ticket_id_or_empty_secret", async () => {
    await expect(qrPayload("0192f0e0-aaaa-7bbb-8ccc-0123456789", secret, 0)).rejects.toThrow(RangeError);
    await expect(qrPayload("0192f0e0aaaa7bbb8ccc0123456789ab00", secret, 0)).rejects.toThrow(RangeError);
    await expect(qrPayload("not-a-guid", secret, 0)).rejects.toThrow(RangeError);
    await expect(qrPayload(ticketId, new Uint8Array(0), 0)).rejects.toThrow(RangeError);
    await expect(qrPayload(ticketId, new Uint8Array(31), 0)).rejects.toThrow(RangeError);
  });

  it("throws on a negative, fractional or non-finite time", async () => {
    await expect(qrPayload(ticketId, secret, -1)).rejects.toThrow(RangeError);
    await expect(qrPayload(ticketId, secret, 1.5)).rejects.toThrow(RangeError);
    await expect(qrPayload(ticketId, secret, Number.NaN)).rejects.toThrow(RangeError);
  });

  it("Generator_uses_Web_Crypto_and_no_network", async () => {
    const fetchSpy = vi.fn();
    vi.stubGlobal("fetch", fetchSpy);
    const importKey = vi.spyOn(globalThis.crypto.subtle, "importKey");
    const sign = vi.spyOn(globalThis.crypto.subtle, "sign");

    await qrPayload(ticketId, secret, 15);

    expect(importKey).toHaveBeenCalledWith("raw", expect.anything(), { name: "HMAC", hash: "SHA-256" }, false, ["sign"]);
    const signed = new TextDecoder().decode(sign.mock.calls[0]![2] as Uint8Array);
    expect(signed).toBe(`${ticketId}|1`);
    expect(fetchSpy).not.toHaveBeenCalled();
  });
});

describe("step arithmetic", () => {
  it("Step_arithmetic_at_boundaries", () => {
    expect(currentStep(0)).toBe(0);
    expect(currentStep(14)).toBe(0);
    expect(currentStep(15)).toBe(1);
    expect(currentStep(29)).toBe(1);
    expect(currentStep(30)).toBe(2);

    expect(msUntilNextStep(0)).toBe(15_000);
    expect(msUntilNextStep(14_000)).toBe(1_000);
    expect(msUntilNextStep(14_999)).toBe(1);
    expect(msUntilNextStep(15_000)).toBe(15_000);
    expect(msUntilNextStep(29_500)).toBe(500);
    expect(msUntilNextStep(30_000)).toBe(15_000);
  });

  it("rejects an invalid time", () => {
    expect(() => currentStep(-1)).toThrow(RangeError);
    expect(() => currentStep(0.5)).toThrow(RangeError);
    expect(() => msUntilNextStep(-1)).toThrow(RangeError);
    expect(() => msUntilNextStep(Number.POSITIVE_INFINITY)).toThrow(RangeError);
  });
});

describe("base64ToBytes", () => {
  it("decodes standard base64 to bytes", () => {
    expect(Array.from(base64ToBytes("AAH/"))).toEqual([0, 1, 255]);
    expect(base64ToBytes(vectors.cases[0]!.secret)).toHaveLength(32);
  });
});
