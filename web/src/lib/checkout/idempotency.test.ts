import { describe, expect, it } from "vitest";
import { ApiError } from "@/lib/errors";
import { createKeyKeeper, isUndecided } from "./idempotency";

const counter = () => {
  let n = 0;
  return () => `key-${++n}`;
};

describe("isUndecided", () => {
  it("treats network failures, 5xx and in_progress as no final answer", () => {
    expect(isUndecided(new TypeError("Failed to fetch"))).toBe(true);
    expect(isUndecided(new ApiError(503, { code: "payment.gateway_unavailable" }))).toBe(true);
    expect(isUndecided(new ApiError(409, { code: "idempotency.in_progress" }))).toBe(true);
  });

  it("treats a stored (4xx) answer as final", () => {
    expect(isUndecided(new ApiError(409, { code: "payment.rejected" }))).toBe(false);
    expect(isUndecided(new ApiError(409, { code: "order.not_payable" }))).toBe(false);
  });
});

describe("createKeyKeeper", () => {
  it("hands out the same key while the request is undecided (double click, network blip)", () => {
    const keeper = createKeyKeeper(counter());
    const first = keeper.acquire();
    expect(keeper.acquire()).toBe(first);
    keeper.settle(new TypeError("Failed to fetch"));
    expect(keeper.acquire()).toBe(first);
    keeper.settle(new ApiError(503, { code: "system.busy" }));
    expect(keeper.acquire()).toBe(first);
  });

  it("hands out a fresh key after a stored error response or a success", () => {
    const keeper = createKeyKeeper(counter());
    const first = keeper.acquire();
    keeper.settle(new ApiError(409, { code: "payment.rejected" }));
    const second = keeper.acquire();
    expect(second).not.toBe(first);
    keeper.settle(null);
    expect(keeper.acquire()).not.toBe(second);
  });

  it("gives a changed request its own key", () => {
    const keeper = createKeyKeeper(counter());
    const a = keeper.acquire("a");
    expect(keeper.acquire("a")).toBe(a);
    expect(keeper.acquire("b")).not.toBe(a);
  });
});
