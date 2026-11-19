import { afterEach, describe, expect, it, vi } from "vitest";

const cache = vi.hoisted(() => ({ life: vi.fn(), tag: vi.fn() }));
vi.mock("next/cache", () => ({ cacheLife: cache.life, cacheTag: cache.tag }));

import { getHomeSections } from "./api";

/** Next.js leaves a cache entry out of prerendered pages when it expires sooner than this, or goes stale sooner than that. */
const MIN_PRERENDERABLE_EXPIRE = 300;
const MIN_PREFETCHABLE_STALE = 30;

afterEach(() => {
  vi.unstubAllGlobals();
  vi.clearAllMocks();
});

describe("getHomeSections", () => {
  // The home has no Suspense boundary (its whole page is the static shell), so even its "no answer" result must be
  // prerenderable, or the route blocks ("Next.js encountered uncached data during prerendering").
  it("keeps an api 404 prerenderable but retries it a second later", async () => {
    vi.stubGlobal("fetch", vi.fn(async () => new Response(null, { status: 404 })));

    expect(await getHomeSections()).toBeNull();

    const life = cache.life.mock.calls.at(-1)![0] as { stale: number; revalidate: number; expire: number };
    expect(life.expire).toBeGreaterThanOrEqual(MIN_PRERENDERABLE_EXPIRE);
    expect(life.stale).toBeGreaterThanOrEqual(MIN_PREFETCHABLE_STALE);
    expect(life.revalidate).toBeLessThanOrEqual(1);
  });

  it("caches the sections for 30 s when the api answers", async () => {
    const home = { hot: [], openingSoon: [], thisWeek: [], upcoming: [] };
    vi.stubGlobal("fetch", vi.fn(async () => Response.json(home)));

    expect(await getHomeSections()).toEqual(home);
    expect(cache.life).toHaveBeenLastCalledWith({ stale: 30, revalidate: 30, expire: 3600 });
  });
});
