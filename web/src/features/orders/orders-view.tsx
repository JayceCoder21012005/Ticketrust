"use client";

import { useInfiniteQuery } from "@tanstack/react-query";
import { PackageOpen } from "lucide-react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { useEffect, useRef, useState } from "react";
import { useAuth } from "@/components/auth/auth-provider";
import { EmptyState } from "@/components/shared/empty-state";
import { ErrorState } from "@/components/shared/error-state";
import { Button } from "@/components/ui/button";
import { Skeleton } from "@/components/ui/skeleton";
import { loginRedirectUrl } from "@/lib/api/session";
import { listMyOrders } from "@/lib/checkout/orders-api";
import type { OrderPage, OrderSummary } from "@/lib/checkout/types";
import { t } from "@/lib/i18n";
import { OrderRow, orderLinkId } from "./order-row";

export const ORDERS_HREF = "/account/orders";

/** The pages in order, each order once: a refetch of all pages can see an order on two of them. */
function flatten(pages: OrderPage[] | undefined): OrderSummary[] {
  return [...new Map((pages ?? []).flatMap((p) => p.items).map((o) => [o.id, o])).values()];
}

/**
 * `/account/orders` (spec 08): the buyer's orders, newest first, a page at a time (keyset: the `nextCursor` of the
 * api). proxy.ts sends signed-out visitors to login; this covers a session hint that outlived its session.
 */
export function OrdersView() {
  const router = useRouter();
  const { status } = useAuth();
  const heading = useRef<HTMLHeadingElement>(null);
  const [announcement, setAnnouncement] = useState("");
  const [focusId, setFocusId] = useState<string | null>(null);

  const query = useInfiniteQuery({
    queryKey: ["my-orders"],
    queryFn: ({ pageParam }) => listMyOrders(pageParam),
    initialPageParam: undefined as string | undefined,
    getNextPageParam: (last) => last.nextCursor ?? undefined,
    enabled: status === "authenticated",
    staleTime: 0,
  });
  const rows = flatten(query.data?.pages);
  const settled = query.isSuccess || query.isError;

  useEffect(() => {
    if (status === "anonymous") router.replace(loginRedirectUrl(ORDERS_HREF));
  }, [status, router]);

  // The heading takes focus once, when the first answer is in (a route change should land on the new content).
  const focused = useRef(false);
  useEffect(() => {
    if (settled && !focused.current) {
      focused.current = true;
      heading.current?.focus();
    }
  }, [settled]);

  // After "Xem thêm" the first new row takes focus: the button may be gone (last page), and a keyboard user lands on what is new.
  useEffect(() => {
    if (focusId) document.getElementById(orderLinkId(focusId))?.focus();
  }, [focusId]);

  async function loadMore() {
    const before = rows.length;
    const result = await query.fetchNextPage();
    const next = flatten(result.data?.pages);
    if (result.isError || next.length <= before) return;
    setAnnouncement(t("orders.loadedMore", "vi", { count: next.length - before, total: next.length }));
    setFocusId(next[before]!.id);
  }

  return (
    <div className="mx-auto flex w-full max-w-3xl flex-col gap-6 px-4 py-10">
      <header className="flex flex-col gap-1 border-b pb-6">
        <h1 ref={heading} tabIndex={-1} className="text-2xl font-bold tracking-tight outline-none sm:text-3xl">
          {t("orders.title")}
        </h1>
        <p className="text-muted-foreground">{t("orders.description")}</p>
      </header>
      {/* The one live region: the loading message first, then how many orders a "Xem thêm" added. */}
      <p role="status" className="sr-only">
        {query.isPending ? t("orders.loading") : announcement}
      </p>
      {query.isError && rows.length === 0 ? (
        <ErrorState title={t("orders.loadFailed")} onRetry={() => void query.refetch()} />
      ) : query.isPending ? (
        <OrdersSkeleton />
      ) : rows.length === 0 ? (
        <EmptyState
          icon={PackageOpen}
          title={t("orders.empty.title")}
          description={t("orders.empty.description")}
          action={
            <Button asChild>
              <Link href="/events">{t("orders.empty.action")}</Link>
            </Button>
          }
        />
      ) : (
        <>
          <ul className="flex flex-col gap-3">
            {rows.map((order) => (
              <OrderRow key={order.id} order={order} />
            ))}
          </ul>
          {query.isFetchNextPageError ? (
            <p role="alert" className="text-center text-sm text-danger">
              {t("orders.loadMoreFailed")}
            </p>
          ) : null}
          {query.hasNextPage ? (
            <Button variant="outline" className="self-center" disabled={query.isFetchingNextPage} onClick={() => void loadMore()}>
              {query.isFetchingNextPage ? t("orders.loadingMore") : t("orders.loadMore")}
            </Button>
          ) : null}
        </>
      )}
    </div>
  );
}

function OrdersSkeleton() {
  return (
    <div aria-busy className="flex flex-col gap-3">
      {[0, 1, 2].map((i) => (
        <Skeleton key={i} className="h-24 w-full rounded-xl" />
      ))}
    </div>
  );
}
