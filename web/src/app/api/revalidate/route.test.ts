import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

const revalidateTag = vi.fn();
vi.mock("next/cache", () => ({ revalidateTag }));

const { POST } = await import("./route");

function request(body: unknown, secret?: string) {
  return new Request("http://localhost/api/revalidate", {
    method: "POST",
    headers: { "content-type": "application/json", ...(secret === undefined ? {} : { "x-revalidate-secret": secret }) },
    body: JSON.stringify(body),
  });
}

describe("POST /api/revalidate", () => {
  beforeEach(() => {
    revalidateTag.mockReset();
    vi.stubEnv("REVALIDATE_SECRET", "s3cret-value-long-enough");
  });
  afterEach(() => vi.unstubAllEnvs());

  it("revalidates catalog tags with stale-while-revalidate ('max')", async () => {
    const response = await POST(request({ tags: ["events:list", "event:0193-abc", "event-slug:son-tung-mtp-live"] }, "s3cret-value-long-enough"));

    expect(response.status).toBe(200);
    expect(revalidateTag.mock.calls).toEqual([
      ["events:list", "max"],
      ["event:0193-abc", "max"],
      ["event-slug:son-tung-mtp-live", "max"],
    ]);
  });

  it.each([undefined, "", "wrong", "s3cret-value-long-enough-but-longer"])("rejects secret %j", async (secret) => {
    const response = await POST(request({ tags: ["events:list"] }, secret));

    expect(response.status).toBe(401);
    expect(revalidateTag).not.toHaveBeenCalled();
  });

  it("is disabled when no secret is configured", async () => {
    vi.stubEnv("REVALIDATE_SECRET", "");

    const response = await POST(request({ tags: ["events:list"] }, ""));

    expect(response.status).toBe(404);
  });

  it("only accepts catalog tags", async () => {
    const response = await POST(request({ tags: ["events:list", "users:all"] }, "s3cret-value-long-enough"));

    expect(response.status).toBe(400);
    expect(revalidateTag).not.toHaveBeenCalled();
  });

  it("rejects a body without tags", async () => {
    expect((await POST(request({}, "s3cret-value-long-enough"))).status).toBe(400);
    expect((await POST(request({ tags: [] }, "s3cret-value-long-enough"))).status).toBe(400);
  });
});
