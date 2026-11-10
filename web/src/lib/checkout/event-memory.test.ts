import { beforeEach, describe, expect, it } from "vitest";
import { recallEvent, rememberEvent } from "./event-memory";

describe("event memory", () => {
  beforeEach(() => localStorage.clear());

  it("remembers an event by id", () => {
    rememberEvent({ id: "e1", slug: "su-kien", title: "Đêm nhạc" });

    expect(recallEvent("e1")).toEqual({ id: "e1", slug: "su-kien", title: "Đêm nhạc" });
    expect(recallEvent("e2")).toBeNull();
  });

  it("ignores stored entries that are not complete events", () => {
    localStorage.setItem("tr.checkout-events", JSON.stringify([{ id: "e1", slug: "su-kien" }, { id: "e2", slug: 5, title: "x" }, null]));

    expect(recallEvent("e1")).toBeNull();
    expect(recallEvent("e2")).toBeNull();
  });
});
