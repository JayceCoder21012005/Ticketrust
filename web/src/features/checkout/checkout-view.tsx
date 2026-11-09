"use client";

import { useQuery } from "@tanstack/react-query";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { useEffect, useRef } from "react";
import { useAuth } from "@/components/auth/auth-provider";
import { ErrorState } from "@/components/shared/error-state";
import { Button } from "@/components/ui/button";
import { Skeleton } from "@/components/ui/skeleton";
import { loginRedirectUrl } from "@/lib/api/session";
import { isPayable } from "@/lib/checkout/order-state";
import { getOrder } from "@/lib/checkout/orders-api";
import { recallEvent } from "@/lib/checkout/event-memory";
import { clockOffsetMs } from "@/lib/clock";
import { ApiError } from "@/lib/errors";
import { t } from "@/lib/i18n";
import { PayPanel } from "./pay-panel";

export const orderKey = (orderId: string) => ["order", orderId] as const;

/** The order and the clock offset measured when it arrived (C10): the countdown runs on server time. */
async function loadOrder(orderId: string) {
  const order = await getOrder(orderId);
  return { order, offsetMs: clockOffsetMs(order.serverNow, Date.now()) };
}

/**
 * `/checkout/[orderId]` (spec 08): the order to pay. An order that is no longer open (paid, cancelled, expired, being
 * refunded) belongs to the result page, which explains each of those.
 */
export function CheckoutView({ orderId }: { orderId: string }) {
  const router = useRouter();
  const { status } = useAuth();
  const signedIn = status === "authenticated";
  const query = useQuery({ queryKey: orderKey(orderId), queryFn: () => loadOrder(orderId), enabled: signedIn, staleTime: 0, gcTime: 0 });
  const open = query.data ? isPayable(query.data.order.status) : null;

  useEffect(() => {
    if (status === "anonymous") router.replace(loginRedirectUrl(`/checkout/${orderId}`));
  }, [status, orderId, router]);

  useEffect(() => {
    if (open === false) router.replace(`/checkout/${orderId}/result`);
  }, [open, orderId, router]);

  if (query.isError) {
    return query.error instanceof ApiError && query.error.status === 404 ? <NotFound /> : <LoadFailed onRetry={() => void query.refetch()} />;
  }

  if (!query.data || !open) {
    return <CheckoutSkeleton />;
  }

  const { order, offsetMs } = query.data;
  return <PayPanel order={order} offsetMs={offsetMs} event={recallEvent(order.eventId)} onReload={() => void query.refetch()} />;
}

function NotFound() {
  const heading = useRef<HTMLHeadingElement>(null);
  useEffect(() => heading.current?.focus(), []);
  return (
    <div className="mx-auto flex w-full max-w-md flex-col items-center gap-3 px-4 py-16 text-center">
      <h1 ref={heading} tabIndex={-1} className="text-xl font-semibold outline-none">
        {t("checkout.notFound.title")}
      </h1>
      <p className="text-sm text-muted-foreground">{t("checkout.notFound.body")}</p>
      <Button asChild>
        <Link href="/events">{t("checkout.notFound.action")}</Link>
      </Button>
    </div>
  );
}

function LoadFailed({ onRetry }: { onRetry: () => void }) {
  return (
    <div className="mx-auto w-full max-w-3xl px-4 py-10">
      <ErrorState title={t("checkout.loadFailed")} onRetry={onRetry} />
    </div>
  );
}

export function CheckoutSkeleton() {
  return (
    <div aria-busy className="mx-auto grid w-full max-w-5xl gap-6 px-4 py-6 md:grid-cols-[1fr_22rem]">
      <p className="sr-only" role="status">
        {t("checkout.loading")}
      </p>
      <Skeleton className="h-72 w-full rounded-xl" />
      <Skeleton className="h-56 w-full rounded-xl" />
    </div>
  );
}
