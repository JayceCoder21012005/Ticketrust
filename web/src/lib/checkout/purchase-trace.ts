/**
 * One trace per purchase (spec 17). The hold starts it, and the order and the payment send the same W3C `traceparent`, so
 * the api, the gateway webhook, the outbox and the email all land in one trace. A few lines instead of a browser OTel SDK:
 * nothing is measured here, the header only names the trace.
 */

const STORAGE_KEY = "tr.purchase-trace";

let current: { key: string; traceParent: string } | undefined;

const hex = (bytes: Uint8Array) => Array.from(bytes, (b) => b.toString(16).padStart(2, "0")).join("");

/** All-zero ids are invalid in W3C trace context: the last byte is bumped so a broken random source still yields a valid header. */
function nonZero(bytes: Uint8Array): Uint8Array {
  if (bytes.every((b) => b === 0)) {
    bytes[bytes.length - 1] = 1;
  }

  return bytes;
}

export function newTraceParent(random: (size: number) => Uint8Array = (size) => crypto.getRandomValues(new Uint8Array(size))): string {
  const bytes = random(24);
  const traceId = nonZero(bytes.slice(0, 16));
  const spanId = nonZero(bytes.slice(16, 24));
  return `00-${hex(traceId)}-${hex(spanId)}-01`;
}

function read(): typeof current {
  try {
    const stored = sessionStorage.getItem(STORAGE_KEY);
    return stored ? (JSON.parse(stored) as typeof current) : undefined;
  } catch {
    return undefined;
  }
}

function write(value: typeof current) {
  try {
    if (value) sessionStorage.setItem(STORAGE_KEY, JSON.stringify(value));
    else sessionStorage.removeItem(STORAGE_KEY);
  } catch {
    // Private mode or storage blocked: the trace lives as long as the page does.
  }
}

/** The hold request names the purchase: the same idempotency key (a retry of that hold) keeps the same trace. */
export function beginPurchaseTrace(holdKey: string): string {
  const known = current ?? read();
  if (known?.key === holdKey) {
    current = known;
    return known.traceParent;
  }

  current = { key: holdKey, traceParent: newTraceParent() };
  write(current);
  return current.traceParent;
}

/** The trace the order and the payment join, if this tab started a purchase. */
export function currentPurchaseTrace(): string | undefined {
  current ??= read();
  return current?.traceParent;
}

/** Tests only: forget the trace (and, unless kept, the stored copy). */
export function resetPurchaseTrace(options: { keepStorage?: boolean } = {}) {
  current = undefined;
  if (!options.keepStorage) write(undefined);
}

/** The header to add, or none. */
export const traceHeader = (traceParent: string | undefined): Record<string, string> => (traceParent ? { traceparent: traceParent } : {});
