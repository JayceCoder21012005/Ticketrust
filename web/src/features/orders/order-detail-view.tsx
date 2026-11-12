"use client";

import { useQuery, useQueryClient, type InfiniteData, type QueryClient } from "@tanstack/react-query";
import { ArrowLeft } from "lucide-react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { useEffect, useRef } from "react";
import { useAuth } from "@/components/auth/auth-provider";
import { ErrorState } from "@/components/shared/error-state";
import { Button } from "@/components/ui/button";
import { Skeleton } from "@/components/ui/skeleton";
import { loginRedirectUrl } from "@/lib/api/session";
import { recallEvent } from "@/lib/checkout/event-memory";
import { isPastDeadline, isPayable, orderCode } from "@/lib/checkout/order-state";
import { getOrder } from "@/lib/checkout/orders-api";
import type { Buyer, Order, OrderPage } from "@/lib/checkout/types";
import { formatDateTime } from "@/lib/format";
import { ApiError } from "@/lib/errors";
import { t } from "@/lib/i18n";
import { OrderLines } from "@/features/checkout/order-lines";
import { ORDERS_HREF } from "./orders-view";
import { StatusBadge } from "./status-badge";
import { StatusTimeline } from "./status-timeline";

type EventName = { title: string; slug: string | null };

/**
 * An order names its event by id only. The list the buyer came from has the title and slug; failing that the seat flow
 * left them in the browser (event-memory); failing that the page says "Sự kiện".
 */
function eventNameOf(client: QueryClient, order: Order): EventName {
  const pages = client.getQueryData<InfiniteData<OrderPage>>(["my-orders"])?.pages ?? [];
  const row = pages.flatMap((p) => p.items).find((o) => o.id === order.id);
  if (row) return { title: row.eventTitle, slug: row.eventSlug };

  const remembered = recallEvent(order.eventId);
  return remembered ? { title: remembered.title, slug: remembered.slug } : { title: t("orders.eventFallback"), slug: null };
}

/**
 * `/account/orders/[orderId]` (spec 08): one order with its lines, buyer and a status timeline. The api only shows an
 * order to its owner (404 otherwise).
 */
export function OrderDetailView({ orderId }: { orderId: string }) {
  const router = useRouter();
  const { status } = useAuth();
  const query = useQuery({ queryKey: ["account-order", orderId], queryFn: () => getOrder(orderId), enabled: status === "authenticated", staleTime: 0 });

  useEffect(() => {
    if (status === "anonymous") router.replace(loginRedirectUrl(`${ORDERS_HREF}/${orderId}`));
  }, [status, orderId, router]);

  // A loaded order stays on screen when a later background refetch fails: the error only matters when there is nothing to show.
  if (query.data) return <OrderDetail order={query.data} />;
  if (query.isError) {
    return query.error instanceof ApiError && query.error.status === 404 ? <NotFound /> : <LoadFailed onRetry={() => void query.refetch()} />;
  }

  return <OrderDetailSkeleton />;
}

function OrderDetail({ order }: { order: Order }) {
  const client = useQueryClient();
  const heading = useRef<HTMLHeadingElement>(null);
  useEffect(() => heading.current?.focus(), []);
  const event = eventNameOf(client, order);

  return (
    <div className="mx-auto flex w-full max-w-3xl flex-col gap-6 px-4 py-10">
      <Button asChild variant="ghost" size="sm" className="-ml-2 self-start">
        <Link href={ORDERS_HREF}>
          <ArrowLeft aria-hidden />
          {t("orders.detail.back")}
        </Link>
      </Button>
      <header className="flex flex-col gap-2 border-b pb-6">
        <div className="flex flex-wrap items-center gap-3">
          <h1 ref={heading} tabIndex={-1} className="text-2xl font-bold tracking-tight outline-none sm:text-3xl">
            {t("checkout.order", "vi", { code: orderCode(order.id) })}
          </h1>
          <StatusBadge status={order.status} />
        </div>
        <p className="text-muted-foreground">
          <span className="font-medium text-foreground">{event.title}</span>
          {` · ${t("orders.placedAt", "vi", { time: formatDateTime(order.createdAt) })}`}
        </p>
        {event.slug ? (
          <Link href={`/events/${event.slug}`} className="w-fit text-sm font-medium text-primary underline-offset-4 hover:underline">
            {t("orders.detail.viewEvent")}
          </Link>
        ) : null}
      </header>
      <StatusTimeline order={order} headingId="order-timeline" />
      <OrderLines lines={order} headingId="order-lines" />
      <BuyerSnapshot buyer={order.buyer} />
      <Actions order={order} />
    </div>
  );
}

function BuyerSnapshot({ buyer }: { buyer: Buyer }) {
  return (
    <section aria-labelledby="order-buyer" className="flex flex-col gap-1 text-sm">
      <h2 id="order-buyer" className="text-base font-semibold">
        {t("orders.detail.buyer")}
      </h2>
      <p>{buyer.name}</p>
      <p className="text-muted-foreground">{buyer.email}</p>
      {buyer.phone ? <p className="text-muted-foreground">{buyer.phone}</p> : null}
    </section>
  );
}

/** Paying while the order is open; a paid order's tickets are in the wallet (spec 10). */
function Actions({ order }: { order: Order }) {
  if (isPayable(order.status)) {
    return isPastDeadline(order) ? (
      <p className="text-sm text-muted-foreground">{t("orders.detail.pastDeadline")}</p>
    ) : (
      <Button asChild className="self-start">
        <Link href={`/checkout/${order.id}`}>{t("orders.continuePay")}</Link>
      </Button>
    );
  }

  if (order.status === "Paid") {
    return (
      <div className="flex flex-col items-start gap-2 text-sm">
        {order.ticketCount ? <p className="font-medium">{t("orders.detail.tickets", "vi", { count: order.ticketCount })}</p> : null}
        <Button asChild>
          <Link href="/account/tickets">{t("orders.detail.viewTickets")}</Link>
        </Button>
      </div>
    );
  }

  return null;
}

function NotFound() {
  const heading = useRef<HTMLHeadingElement>(null);
  useEffect(() => heading.current?.focus(), []);
  return (
    <div className="mx-auto flex w-full max-w-md flex-col items-center gap-3 px-4 py-16 text-center">
      <h1 ref={heading} tabIndex={-1} className="text-xl font-semibold outline-none">
        {t("orders.detail.notFound.title")}
      </h1>
      <p className="text-sm text-muted-foreground">{t("orders.detail.notFound.body")}</p>
      <Button asChild>
        <Link href={ORDERS_HREF}>{t("orders.detail.notFound.action")}</Link>
      </Button>
    </div>
  );
}

function LoadFailed({ onRetry }: { onRetry: () => void }) {
  const heading = useRef<HTMLHeadingElement>(null);
  useEffect(() => heading.current?.focus(), []);
  return (
    <div className="mx-auto w-full max-w-3xl px-4 py-10">
      <ErrorState title={t("orders.detail.loadFailed")} onRetry={onRetry} headingRef={heading} />
    </div>
  );
}

export function OrderDetailSkeleton() {
  return (
    <div aria-busy className="mx-auto flex w-full max-w-3xl flex-col gap-4 px-4 py-10">
      <p role="status" className="sr-only">
        {t("orders.detail.loading")}
      </p>
      <Skeleton className="h-10 w-2/3" />
      <Skeleton className="h-48 w-full rounded-xl" />
      <Skeleton className="h-40 w-full rounded-xl" />
    </div>
  );
}
