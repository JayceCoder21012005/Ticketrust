import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { clearAdmission, loadAdmission, saveAdmission } from "./admission-store";

describe("admission store", () => {
  beforeEach(() => {
    localStorage.clear();
    vi.useFakeTimers();
    vi.setSystemTime(new Date("2026-12-01T10:00:00Z"));
  });
  afterEach(() => vi.useRealTimers());

  const admission = { eventId: "e1", slug: "show", token: "tok", expiresAt: "2026-12-01T10:10:00Z" };

  it("round-trips the latest admission", () => {
    saveAdmission(admission);

    expect(loadAdmission()).toEqual(admission);
  });

  it("forgets an admission whose window has ended", () => {
    saveAdmission(admission);
    vi.setSystemTime(new Date("2026-12-01T10:10:01Z"));

    expect(loadAdmission()).toBeNull();
  });

  it("clears only the matching event", () => {
    saveAdmission(admission);

    clearAdmission("other");
    expect(loadAdmission()).not.toBeNull();
    clearAdmission("e1");
    expect(loadAdmission()).toBeNull();
  });

  it("ignores corrupt data", () => {
    localStorage.setItem("tr.admission", "{not json");

    expect(loadAdmission()).toBeNull();
  });

  it("never throws when storage is unavailable", () => {
    vi.spyOn(Storage.prototype, "setItem").mockImplementation(() => {
      throw new Error("blocked");
    });
    vi.spyOn(Storage.prototype, "getItem").mockImplementation(() => {
      throw new Error("blocked");
    });

    expect(() => saveAdmission(admission)).not.toThrow();
    expect(loadAdmission()).toBeNull();
  });
});
