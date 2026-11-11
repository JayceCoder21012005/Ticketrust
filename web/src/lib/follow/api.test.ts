import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { ApiError } from "@/lib/errors";
import { getFollowing, setFollowing } from "./api";

const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { "content-type": "application/json" } });
const problem = (status: number, code: string) => new Response(JSON.stringify({ status, code }), { status, headers: { "content-type": "application/problem+json" } });
const calls: Request[] = [];
const fetchMock = vi.fn();

const answer = (response: () => Response) =>
  fetchMock.mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) => {
    calls.push(input instanceof Request ? input.clone() : new Request(new URL(String(input), "http://localhost:3000"), init));
    return response();
  });

beforeEach(() => {
  calls.length = 0;
  vi.stubGlobal("fetch", fetchMock);
});

afterEach(() => vi.unstubAllGlobals());

describe("getFollowing", () => {
  it("reads the state of one event", async () => {
    answer(() => json({ following: true }));

    await expect(getFollowing("e1")).resolves.toBe(true);

    expect(calls[0]!.method).toBe("GET");
    expect(new URL(calls[0]!.url).pathname).toBe("/api/v1/events/e1/follow");
  });

  it("throws the api's problem when it cannot answer", async () => {
    answer(() => problem(404, "event.not_found"));

    await expect(getFollowing("e1")).rejects.toMatchObject({ status: 404, code: "event.not_found" });
  });
});

describe("setFollowing", () => {
  it.each([
    [true, "POST"],
    [false, "DELETE"],
  ])("following=%s sends %s", async (following, method) => {
    answer(() => new Response(null, { status: 204 }));

    await setFollowing("e1", following);

    expect(calls[0]!.method).toBe(method);
    expect(new URL(calls[0]!.url).pathname).toBe("/api/v1/events/e1/follow");
  });

  it("throws an ApiError carrying the code when the event is not followable", async () => {
    answer(() => problem(409, "event.not_followable"));

    const error = await setFollowing("e1", true).catch((e: unknown) => e);

    expect(error).toBeInstanceOf(ApiError);
    expect(error).toMatchObject({ status: 409, code: "event.not_followable" });
  });
});
