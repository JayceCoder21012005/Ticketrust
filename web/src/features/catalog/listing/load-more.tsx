"use client";

import { Fragment, useState, type ReactNode } from "react";
import { Button } from "@/components/ui/button";
import { t } from "@/lib/i18n";
import { loadMoreEvents } from "./actions";

type LoadMoreProps = {
  /** Api query of the first page (filters already resolved). */
  query: Record<string, string>;
  cursor: string;
};

/**
 * "Xem thêm" keyset pagination (spec 04: no page numbers). The server rendered page 1; each click asks a server
 * action for the next page as ready-made cards and appends them.
 */
export function LoadMore({ query, cursor: firstCursor }: LoadMoreProps) {
  const [pages, setPages] = useState<ReactNode[]>([]);
  const [cursor, setCursor] = useState<string | null>(firstCursor);
  const [status, setStatus] = useState<"idle" | "loading" | "error">("idle");

  const load = async () => {
    if (!cursor) return;
    setStatus("loading");
    try {
      const page = await loadMoreEvents(query, cursor);
      setPages((current) => [...current, page.cards]);
      setCursor(page.nextCursor);
      setStatus("idle");
    } catch {
      setStatus("error");
    }
  };

  return (
    <>
      {pages.map((cards, i) => (
        <Fragment key={i}>{cards}</Fragment>
      ))}
      {cursor ? (
        <li className="col-span-full flex flex-col items-center gap-2 pt-4">
          <Button variant="outline" size="lg" onClick={load} disabled={status === "loading"} className="min-w-40">
            {status === "loading" ? t("catalog.listing.loadingMore") : t("catalog.listing.loadMore")}
          </Button>
          <p role="status" className="text-sm text-destructive">
            {status === "error" ? t("catalog.listing.loadMoreError") : null}
          </p>
        </li>
      ) : null}
    </>
  );
}
