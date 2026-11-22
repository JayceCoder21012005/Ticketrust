"use client";

import { Ban, CircleCheck, CircleX, Clock, LoaderCircle, Undo2, type LucideIcon } from "lucide-react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { useEffect, useRef, type ReactNode } from "react";
import { useAuth } from "@/components/auth/auth-provider";
import { ErrorState } from "@/components/shared/error-state";
import { Button } from "@/components/ui/button";
import { Skeleton } from "@/components/ui/skeleton";
import { useUserHub } from "@/features/notifications/use-user-hub";
import { loginRedirectUrl } from "@/lib/api/session";
import { recallEvent } from "@/lib/checkout/event-memory";
import { confirmPayment } from "@/lib/checkout/orders-api";
import { isPastDeadline, refundReasonKey, resultKindOf, type ResultKind } from "@/lib/checkout/order-state";
import type { Order } from "@/lib/checkout/types";
import { useOrderPolling } from "@/lib/checkout/use-order-polling";
import { t } from "@/lib/i18n.buyer";
import { cn } from "@/lib/utils";
import { Confetti } from "./confetti";

/** Where the tickets are listed once that page exists (spec 08: `/account/orders`). */
export const ORDERS_HREF = "/account/orders";

type View = ResultKind | "timeout";

/**
 * `/checkout/[orderId]/result` (spec 08): where the gateway sends the buyer back. It follows the order until it
 * settles (the webhook may arrive after the redirect) and says honestly what happened, including a refund.
 */
export function ResultView({ orderId }: { orderId: string }) {
  const router = useRouter();
  const { status } = useAuth();

  useEffect(() => {
    if (status === "anonymous") router.replace(loginRedirectUrl(`/checkout/${orderId}/result`));
  }, [status, orderId, router]);

  return status === "authenticated" ? <PollingResult orderId={orderId} /> : <ResultSkeleton />;
}

function PollingResult({ orderId }: { orderId: string }) {
  const { order, phase, refresh, refetch } = useOrderPolling(orderId);
  useConfirmOnReturn(orderId, order, refetch);

  // The user hub (buyer layout) says the moment the order changes (spec 09): read it now instead of at the next beat.
  // Polling stays as it was: with no hub, or one that never connects, nothing is pushed and the beats do the work.
  const { subscribeOrderStatus } = useUserHub();
  useEffect(() => subscribeOrderStatus(orderId, refetch), [subscribeOrderStatus, orderId, refetch]);

  if (phase === "error" && !order) {
    return <Missing />;
  }

  if (!order) {
    return phase === "timeout" ? (
      <div className="mx-auto w-full max-w-lg px-4 py-10">
        <ErrorState title={t("checkout.result.loadFailed")} onRetry={refresh} />
      </div>
    ) : (
      <ResultSkeleton />
    );
  }

  const view: View = phase === "timeout" ? "timeout" : resultKindOf(order.status);
  return (
    <div className="mx-auto flex w-full max-w-lg flex-1 flex-col justify-center px-4 py-10">
      {/* The state is spoken once when it changes; the heading below takes focus for keyboard users. */}
      <p role="status" className="sr-only">
        {t(`checkout.result.${view}.title`)}
      </p>
      {view === "success" ? <Confetti /> : null}
      <StateCard key={view} view={view} order={order} onRefresh={refresh} />
    </div>
  );
}

/**
 * When the result page asks the server to confirm the payment (spec 18), counted from the moment the order is seen awaiting
 * it: at once, then twice more while the gateway may still be finishing. After that, polling and webhooks carry on alone.
 */
export const CONFIRM_DELAYS_MS = [0, 3_000, 8_000] as const;

/**
 * Back from the gateway (spec 18): while the order awaits its payment, ask the server to confirm it with the gateway, on the
 * {@link CONFIRM_DELAYS_MS} schedule and one call at a time. A failure is ignored (polling goes on); an answer that moved the
 * order on is read at once. Nothing is asked once the order has moved on.
 */
function useConfirmOnReturn(orderId: string, order: Order | null, refetch: () => void) {
  const awaiting = order?.status === "AwaitingPayment";

  useEffect(() => {
    if (!awaiting) return;
    let inFlight = false;
    const confirm = () => {
      if (inFlight) return;
      inFlight = true;
      Promise.resolve()
        .then(() => confirmPayment(orderId))
        .then((confirmed) => {
          if (confirmed && confirmed.status !== "AwaitingPayment") refetch();
        })
        .catch(() => undefined)
        .finally(() => {
          inFlight = false;
        });
    };
    const timers = CONFIRM_DELAYS_MS.map((delay) => setTimeout(confirm, delay));
    return () => timers.forEach(clearTimeout);
  }, [orderId, awaiting, refetch]);
}

const ICONS: Record<View, { icon: LucideIcon; tone: string; spin?: boolean }> = {
  confirming: { icon: LoaderCircle, tone: "text-primary", spin: true },
  success: { icon: CircleCheck, tone: "text-success" },
  failed: { icon: CircleX, tone: "text-danger" },
  expired: { icon: Clock, tone: "text-muted-foreground" },
  cancelled: { icon: Ban, tone: "text-muted-foreground" },
  refunding: { icon: Undo2, tone: "text-info" },
  refunded: { icon: Undo2, tone: "text-success" },
  timeout: { icon: Clock, tone: "text-warning" },
};

/** Remounted for each state (see `key`), so the new heading is focused every time the outcome changes. */
function StateCard({ view, order, onRefresh }: { view: View; order: Order; onRefresh: () => void }) {
  const heading = useRef<HTMLHeadingElement>(null);
  useEffect(() => heading.current?.focus(), []);
  const { icon: Icon, tone, spin } = ICONS[view];

  return (
    <section aria-labelledby="result-title" className="flex flex-col items-center gap-4 rounded-2xl border bg-card p-8 text-center shadow-sm">
      <Icon aria-hidden className={cn("size-12", tone, spin && "animate-spin")} />
      <h1 id="result-title" ref={heading} tabIndex={-1} className="text-2xl font-bold tracking-tight outline-none">
        {t(`checkout.result.${view}.title`)}
      </h1>
      <Body view={view} order={order} />
      <div className="flex flex-wrap justify-center gap-2">
        <Actions view={view} order={order} onRefresh={onRefresh} />
      </div>
    </section>
  );
}

function Body({ view, order }: { view: View; order: Order }) {
  if (view === "refunding") {
    return (
      <div className="flex max-w-sm flex-col gap-2 text-sm text-muted-foreground">
        <p>{t("checkout.result.refunding.lead")}</p>
        <p className="font-medium text-foreground">{t(refundReasonKey(order.refundReason))}</p>
        <p>{t("checkout.result.refunding.tail")}</p>
      </div>
    );
  }

  return (
    <div className="flex max-w-sm flex-col gap-2 text-sm text-muted-foreground">
      <p>{t(`checkout.result.${view}.body`)}</p>
      {view === "success" && order.ticketCount ? <p className="font-medium text-foreground">{t("checkout.result.success.tickets", "vi", { count: order.ticketCount })}</p> : null}
      {view === "failed" && isPastDeadline(order) ? <p>{t("checkout.result.failed.gone")}</p> : null}
    </div>
  );
}

function Actions({ view, order, onRefresh }: { view: View; order: Order; onRefresh: () => void }): ReactNode {
  const event = recallEvent(order.eventId);
  const orders = (
    <Button asChild variant="outline">
      <Link href={ORDERS_HREF}>{t("checkout.result.ordersLink")}</Link>
    </Button>
  );
  const seats = (
    <Button asChild>
      <Link href={event ? `/events/${event.slug}/seats` : "/events"}>{event ? t("checkout.result.seatsAgain") : t("checkout.expired.events")}</Link>
    </Button>
  );
  const open = !isPastDeadline(order);

  switch (view) {
    case "success":
      return (
        <Button asChild>
          <Link href={ORDERS_HREF}>{t("checkout.result.success.action")}</Link>
        </Button>
      );
    case "failed":
      return open ? (
        <Button asChild>
          <Link href={`/checkout/${order.id}`}>{t("checkout.result.failed.retry")}</Link>
        </Button>
      ) : (
        seats
      );
    case "expired":
    case "cancelled":
      return seats;
    case "refunding":
    case "refunded":
      return orders;
    case "timeout":
      return (
        <>
          <Button onClick={onRefresh}>{t("checkout.result.timeout.refresh")}</Button>
          {orders}
          {open ? (
            <Button asChild variant="outline">
              <Link href={`/checkout/${order.id}`}>{t("checkout.result.timeout.backToPay")}</Link>
            </Button>
          ) : null}
        </>
      );
    default:
      return null;
  }
}

function Missing() {
  const heading = useRef<HTMLHeadingElement>(null);
  useEffect(() => heading.current?.focus(), []);
  return (
    <div className="mx-auto flex w-full max-w-md flex-col items-center gap-3 px-4 py-16 text-center">
      <h1 ref={heading} tabIndex={-1} className="text-xl font-semibold outline-none">
        {t("checkout.notFound.title")}
      </h1>
      <p className="text-sm text-muted-foreground">{t("checkout.notFound.body")}</p>
      <Button asChild>
        <Link href={ORDERS_HREF}>{t("checkout.result.ordersLink")}</Link>
      </Button>
    </div>
  );
}

export function ResultSkeleton() {
  return (
    <div aria-busy className="mx-auto flex w-full max-w-lg flex-1 flex-col justify-center gap-3 px-4 py-10">
      <p role="status" className="sr-only">
        {t("checkout.result.loading")}
      </p>
      <Skeleton className="h-64 w-full rounded-2xl" />
    </div>
  );
}

