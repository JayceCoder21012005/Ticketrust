import { createHash, timingSafeEqual } from "node:crypto";
import { revalidateTag } from "next/cache";

/** Only the catalog's own tags can be evicted (spec 04): `events:list`, `event:{id}`, `event-slug:{slug}`. */
const TAG = /^(events:list|event:[0-9a-f-]{1,64}|event-slug:[a-z0-9-]{1,160})$/;

/** Hashing first gives equal-length buffers, so the comparison takes the same time whatever the input. */
function sameSecret(given: string, expected: string): boolean {
  const digest = (value: string) => createHash("sha256").update(value).digest();
  return timingSafeEqual(digest(given), digest(expected));
}

/**
 * Called by the api after an event changes (spec 05), next to evicting its own HybridCache entries.
 * `'max'` serves the stale page while the next request regenerates it in the background.
 */
export async function POST(request: Request): Promise<Response> {
  const secret = process.env.REVALIDATE_SECRET;
  if (!secret) {
    return new Response(null, { status: 404 });
  }

  if (!sameSecret(request.headers.get("x-revalidate-secret") ?? "", secret)) {
    return Response.json({ code: "revalidate.unauthorized" }, { status: 401 });
  }

  const body = (await request.json().catch(() => null)) as { tags?: unknown } | null;
  const tags = body?.tags;
  if (!Array.isArray(tags) || tags.length === 0 || tags.length > 20 || !tags.every((tag) => typeof tag === "string" && TAG.test(tag))) {
    return Response.json({ code: "revalidate.invalid_tags" }, { status: 400 });
  }

  for (const tag of tags as string[]) {
    revalidateTag(tag, "max");
  }

  return Response.json({ revalidated: tags });
}
