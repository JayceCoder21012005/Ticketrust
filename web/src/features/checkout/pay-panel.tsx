"use client";

import { Loader2 } from "lucide-react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { AlertDialog } from "radix-ui";
import { useCallback, useEffect, useId, useRef, useState } from "react";
import { FormAlert } from "@/features/auth/form-alert";
import { HoldCountdown } from "@/features/seats/hold-panel";
import { Button } from "@/components/ui/button";
import { isCancellable } from "@/lib/checkout/order-state";
import { cancelOrder } from "@/lib/checkout/orders-api";
import type { RememberedEvent } from "@/lib/checkout/event-memory";
import type { Order } from "@/lib/checkout/types";
import { ApiError, toFriendlyMessage } from "@/lib/errors";
import { t } from "@/lib/i18n";
import { OrderLines } from "./order-lines";
import { usePay } from "./use-pay";

type PayPanelProps = { order: Order; offsetMs: number; event: RememberedEvent | null; onReload: () => void };

const END_CODES = new Set(["order.expired", "order.hold_expired"]);

/** Short, stable reference the buyer can quote: the start of the order id. */
const orderCode = (id: string) => `#${id.slice(0, 8).toUpperCase()}`;

/**
 * The payable order: countdown pinned under the header, what is being bought, who is buying, the terms, "Thanh toán"
 * and (while nothing has been paid for yet) "Huỷ đơn".
 */
export function PayPanel({ order, offsetMs, event, onReload }: PayPanelProps) {
  const [ended, setEnded] = useState(false);
  const onExpire = useCallback(() => setEnded(true), []);
  // The order may have moved on (a payment started elsewhere, a webhook): look again, once per refused attempt.
  const pay = usePay(order.id, (e) => {
    if (e instanceof ApiError && e.code === "order.not_payable") onReload();
  });
  const endedByApi = pay.error instanceof ApiError && END_CODES.has(pay.error.code ?? "");
  const over = ended || endedByApi;
  const seatsHref = event ? `/events/${event.slug}/seats` : null;

  return (
    <div className="flex flex-1 flex-col">
      <div className="sticky top-16 z-30 border-b bg-background/95 backdrop-blur">
        <div className="mx-auto w-full max-w-5xl px-4 py-2">
          <HoldCountdown expiresAt={order.expiresAt} offsetMs={offsetMs} onExpire={onExpire} label={t("checkout.countdown.label")} lowLabel={t("checkout.countdown.low")} />
        </div>
      </div>
      <div className="mx-auto grid w-full max-w-5xl gap-6 px-4 py-6 md:grid-cols-[1fr_22rem]">
        <div className="flex flex-col gap-6">
          <header className="flex flex-col gap-1">
            <h1 className="text-2xl font-bold tracking-tight">{t("checkout.title")}</h1>
            <p className="text-sm text-muted-foreground">
              {event ? <span className="font-medium text-foreground">{`${event.title} · `}</span> : null}
              {t("checkout.order", "vi", { code: orderCode(order.id) })}
            </p>
          </header>
          <OrderLines lines={order} headingId="order-summary" />
          <BuyerInfo buyer={order.buyer} />
        </div>
        {over ? <ExpiredState seatsHref={seatsHref} /> : <PayCard order={order} pay={pay} onCancelled={onReload} />}
      </div>
    </div>
  );
}

function BuyerInfo({ buyer }: { buyer: Order["buyer"] }) {
  return (
    <section aria-labelledby="buyer-info" className="flex flex-col gap-2">
      <h2 id="buyer-info" className="text-base font-semibold">
        {t("checkout.summary.buyer")}
      </h2>
      <dl className="grid grid-cols-[auto_1fr] gap-x-4 gap-y-1 text-sm">
        <dt className="text-muted-foreground">{t("checkout.buyerStep.name")}</dt>
        <dd>{buyer.name}</dd>
        <dt className="text-muted-foreground">{t("checkout.buyerStep.email")}</dt>
        <dd className="break-all">{buyer.email}</dd>
        {buyer.phone ? (
          <>
            <dt className="text-muted-foreground">{t("checkout.buyerStep.phone").replace(/ \(.*\)$/, "")}</dt>
            <dd>{buyer.phone}</dd>
          </>
        ) : null}
      </dl>
    </section>
  );
}

function PayCard({ order, pay, onCancelled }: { order: Order; pay: ReturnType<typeof usePay>; onCancelled: () => void }) {
  const termsId = useId();
  const terms = useRef<HTMLInputElement>(null);
  const button = useRef<HTMLButtonElement>(null);
  const [accepted, setAccepted] = useState(false);
  const [needTerms, setNeedTerms] = useState(false);
  const message = pay.error ? toFriendlyMessage(pay.error) : null;

  useEffect(() => {
    // Disabling the button while paying drops its focus: give it back once the answer is an error.
    if (pay.error) button.current?.focus();
  }, [pay.error]);

  const onPay = () => {
    if (!accepted) {
      setNeedTerms(true);
      terms.current?.focus();
      return;
    }

    void pay.pay();
  };

  return (
    <aside className="flex h-fit flex-col gap-4 rounded-xl border bg-card p-4">
      {order.status === "PaymentFailed" ? (
        <p className="rounded-md border border-warning/40 bg-warning/10 px-3 py-2 text-sm">{t("checkout.previousFailed")}</p>
      ) : null}
      <div className="flex flex-col gap-1">
        <div className="flex items-start gap-2">
          <input
            ref={terms}
            id={termsId}
            type="checkbox"
            checked={accepted}
            aria-invalid={needTerms && !accepted}
            aria-describedby={needTerms && !accepted ? `${termsId}-error` : undefined}
            onChange={(e) => setAccepted(e.target.checked)}
            className="mt-0.5 size-4 shrink-0 accent-primary"
          />
          <label htmlFor={termsId} className="text-sm">
            {t("checkout.terms.label")}
          </label>
        </div>
        {needTerms && !accepted ? (
          <p id={`${termsId}-error`} role="alert" className="text-sm text-danger">
            {t("checkout.terms.required")}
          </p>
        ) : null}
      </div>
      <FormAlert message={message} />
      <Button ref={button} size="lg" disabled={pay.busy} onClick={onPay}>
        {pay.busy ? <Loader2 aria-hidden className="animate-spin" /> : null}
        {pay.busy ? t("checkout.paying") : t("checkout.pay")}
      </Button>
      {isCancellable(order.status) ? <CancelOrder orderId={order.id} onFailed={onCancelled} /> : null}
    </aside>
  );
}

/** "Huỷ đơn": confirm first; the seats go back at once. Only offered while the order is `Pending` (api: 409 after that). */
function CancelOrder({ orderId, onFailed }: { orderId: string; onFailed: () => void }) {
  const router = useRouter();
  const [open, setOpen] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const confirm = async () => {
    setBusy(true);
    setError(null);
    try {
      await cancelOrder(orderId);
      router.replace(`/checkout/${orderId}/result`);
    } catch (e) {
      setError(toFriendlyMessage(e));
      setOpen(false);
      setBusy(false);
      // The order may have moved on (a payment started elsewhere): look again.
      if (e instanceof ApiError && e.code === "order.not_payable") onFailed();
    }
  };

  return (
    <>
      <FormAlert message={error} />
      <AlertDialog.Root open={open} onOpenChange={setOpen}>
        <AlertDialog.Trigger asChild>
          <Button variant="outline">{t("checkout.cancel.action")}</Button>
        </AlertDialog.Trigger>
        <AlertDialog.Portal>
          <AlertDialog.Overlay className="fixed inset-0 z-50 bg-black/50" />
          <AlertDialog.Content className="fixed top-1/2 left-1/2 z-50 grid w-[calc(100%-2rem)] max-w-sm -translate-x-1/2 -translate-y-1/2 gap-3 rounded-xl border bg-background p-6 shadow-lg">
            <AlertDialog.Title className="text-lg font-semibold">{t("checkout.cancel.title")}</AlertDialog.Title>
            <AlertDialog.Description className="text-sm text-muted-foreground">{t("checkout.cancel.body")}</AlertDialog.Description>
            <div className="flex justify-end gap-2">
              <AlertDialog.Cancel asChild>
                <Button variant="outline">{t("checkout.cancel.keep")}</Button>
              </AlertDialog.Cancel>
              <Button variant="destructive" disabled={busy} onClick={() => void confirm()}>
                {busy ? t("checkout.cancel.cancelling") : t("checkout.cancel.confirm")}
              </Button>
            </div>
          </AlertDialog.Content>
        </AlertDialog.Portal>
      </AlertDialog.Root>
    </>
  );
}

/** The payment window is over: nothing to pay any more, the way on is to choose seats again. */
function ExpiredState({ seatsHref }: { seatsHref: string | null }) {
  const heading = useRef<HTMLHeadingElement>(null);
  useEffect(() => heading.current?.focus(), []);

  return (
    <aside className="flex h-fit flex-col gap-3 rounded-xl border bg-card p-4">
      <h2 ref={heading} tabIndex={-1} className="text-lg font-semibold outline-none">
        {t("checkout.expired.title")}
      </h2>
      <p className="text-sm text-muted-foreground">{t("checkout.expired.body")}</p>
      <Button asChild>
        <Link href={seatsHref ?? "/events"}>{seatsHref ? t("checkout.expired.action") : t("checkout.expired.events")}</Link>
      </Button>
    </aside>
  );
}
