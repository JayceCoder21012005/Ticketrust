import { afterEach, describe, expect, it, vi } from "vitest";

// The cleanup module cannot be loaded (a deployment swapped the chunks, the network dropped mid-logout).
vi.mock("./register-sw", () => {
  throw new Error("chunk failed to load");
});

const { forgetOfflineTicketsLater } = await import("./forget-offline");

afterEach(() => {
  vi.unstubAllGlobals();
  localStorage.clear();
});

describe("forgetOfflineTicketsLater", () => {
  it("falls back to deleting the database, with no import, when the cleanup cannot be loaded", async () => {
    const deleteDatabase = vi.fn();
    vi.stubGlobal("indexedDB", { deleteDatabase });
    localStorage.setItem("tr.wallet.owner", "a");

    await forgetOfflineTicketsLater();

    expect(deleteDatabase).toHaveBeenCalledWith("ticketrush-tickets");
    expect(localStorage.getItem("tr.wallet.owner")).toBeNull();
  });

  it("loads nothing when asked only if something was stored and no marker exists", async () => {
    const deleteDatabase = vi.fn();
    vi.stubGlobal("indexedDB", { deleteDatabase });

    await forgetOfflineTicketsLater({ onlyIfStored: true });

    expect(deleteDatabase).not.toHaveBeenCalled();
  });
});
