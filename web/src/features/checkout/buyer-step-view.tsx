"use client";

import { useQuery } from "@tanstack/react-query";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { useCallback, useEffect, useRef, useState } from "react";
import { useForm } from "react-hook-form";
import { useAuth } from "@/components/auth/auth-provider";
import { ErrorState } from "@/components/shared/error-state";
import { Button } from "@/components/ui/button";
import { Field, FieldError, FieldGroup, FieldLabel } from "@/components/ui/field";
import { Input } from "@/components/ui/input";
import { FormAlert } from "@/features/auth/form-alert";
import { HoldCountdown } from "@/features/seats/hold-panel";
import { loginRedirectUrl } from "@/lib/api/session";
import { lazyResolver } from "@/lib/auth/lazy-resolver";
import type { BuyerInput } from "@/lib/auth/schemas";
import { rememberEvent, type RememberedEvent } from "@/lib/checkout/event-memory";
import { createKeyKeeper } from "@/lib/checkout/idempotency";
import { createOrder } from "@/lib/checkout/orders-api";
import { clockOffsetMs } from "@/lib/clock";
import { ApiError, toFriendlyMessage } from "@/lib/errors";
import { t } from "@/lib/i18n";
import { getMyHold } from "@/lib/seats/holds-api";
import type { Hold } from "@/lib/seats/types";
import { CheckoutSkeleton } from "./checkout-view";
import { OrderLines } from "./order-lines";

const HOLD_CODES = new Set(["order.hold_expired", "order.hold_invalid"]);

/** The hold and the clock offset measured when it arrived (C10). */
async function loadHold(eventId: string) {
  const hold = await getMyHold(eventId);
  return hold && { hold, offsetMs: clockOffsetMs(hold.serverNow, Date.now()) };
}

/**
 * `/events/[slug]/checkout` (spec 08): the step between the seats and the order. An order keeps the buyer it was made
 * for, so who is buying is asked here, before `POST /orders`; the order page then only shows it and takes the payment.
 */
export function BuyerStepView({ event }: { event: RememberedEvent }) {
  const router = useRouter();
  const { status, user } = useAuth();
  const signedIn = status === "authenticated";
  const query = useQuery({ queryKey: ["hold", event.id], queryFn: () => loadHold(event.id), enabled: signedIn, staleTime: 0, gcTime: 0 });
  const seatsHref = `/events/${event.slug}/seats`;

  useEffect(() => {
    if (status === "anonymous") router.replace(loginRedirectUrl(`/events/${event.slug}/checkout`));
  }, [status, event.slug, router]);

  if (query.isError) {
    return (
      <div className="mx-auto w-full max-w-3xl px-4 py-10">
        <ErrorState title={t("checkout.buyerStep.loadFailed")} onRetry={() => void query.refetch()} />
      </div>
    );
  }

  if (!query.isSuccess) {
    return <CheckoutSkeleton />;
  }

  if (!query.data) {
    return <NoHold seatsHref={seatsHref} />;
  }

  const { hold, offsetMs } = query.data;
  const buyer = { fullName: user?.fullName ?? "", email: user?.email ?? "", phoneNumber: user?.phoneNumber ?? "" };
  return <BuyerStep event={event} hold={hold} offsetMs={offsetMs} initial={buyer} onCreated={(id) => router.push(`/checkout/${id}`)} />;
}

function NoHold({ seatsHref }: { seatsHref: string }) {
  const heading = useRef<HTMLHeadingElement>(null);
  useEffect(() => heading.current?.focus(), []);
  return (
    <div className="mx-auto flex w-full max-w-md flex-col items-center gap-3 px-4 py-16 text-center">
      <h1 ref={heading} tabIndex={-1} className="text-xl font-semibold outline-none">
        {t("checkout.buyerStep.noHold.title")}
      </h1>
      <p className="text-sm text-muted-foreground">{t("checkout.buyerStep.noHold.body")}</p>
      <Button asChild>
        <Link href={seatsHref}>{t("checkout.buyerStep.noHold.action")}</Link>
      </Button>
    </div>
  );
}

type StepProps = { event: RememberedEvent; hold: Hold; offsetMs: number; initial: BuyerInput; onCreated: (orderId: string) => void };

function BuyerStep({ event, hold, offsetMs, initial, onCreated }: StepProps) {
  const [keys] = useState(createKeyKeeper);
  const [error, setError] = useState<string | null>(null);
  const [holdGone, setHoldGone] = useState(false);
  const [ended, setEnded] = useState(false);
  const onExpire = useCallback(() => setEnded(true), []);
  const form = useForm<BuyerInput>({ resolver: lazyResolver<BuyerInput>((s) => s.buyerSchema), defaultValues: initial });
  const { errors, isSubmitting } = form.formState;
  const gone = holdGone || ended;

  const onSubmit = form.handleSubmit(async (values) => {
    setError(null);
    const body = { eventId: event.id, holdId: hold.holdId, buyer: { name: values.fullName, email: values.email, phone: values.phoneNumber || null } };
    try {
      // The same order asked for again (a network blip) shares one key; a changed buyer or a refusal starts a new one.
      const order = await createOrder(body, keys.acquire(JSON.stringify(body)));
      keys.settle(null);
      rememberEvent(event);
      onCreated(order.id);
    } catch (e) {
      keys.settle(e);
      if (e instanceof ApiError && HOLD_CODES.has(e.code ?? "")) setHoldGone(true);
      else setError(toFriendlyMessage(e));
    }
  });

  return (
    <div className="mx-auto grid w-full max-w-5xl gap-6 px-4 py-6 md:grid-cols-[1fr_22rem]">
      <form onSubmit={onSubmit} noValidate className="flex flex-col gap-6" aria-labelledby="buyer-step-title">
        <header className="flex flex-col gap-1">
          <h1 id="buyer-step-title" className="text-2xl font-bold tracking-tight">
            {t("checkout.buyerStep.title")}
          </h1>
          <p className="text-sm text-muted-foreground">
            <span className="font-medium text-foreground">{event.title}</span>
            {` · ${t("checkout.buyerStep.description")}`}
          </p>
        </header>
        {gone ? <HoldGone seatsHref={`/events/${event.slug}/seats`} /> : null}
        <FormAlert message={error} />
        <FieldGroup>
          <Field data-invalid={!!errors.fullName}>
            <FieldLabel htmlFor="buyer-name">{t("checkout.buyerStep.name")}</FieldLabel>
            <Input id="buyer-name" autoComplete="name" aria-invalid={!!errors.fullName} {...form.register("fullName")} />
            <FieldError errors={[errors.fullName]} />
          </Field>
          <Field data-invalid={!!errors.email}>
            <FieldLabel htmlFor="buyer-email">{t("checkout.buyerStep.email")}</FieldLabel>
            <Input id="buyer-email" type="email" autoComplete="email" aria-invalid={!!errors.email} {...form.register("email")} />
            <FieldError errors={[errors.email]} />
          </Field>
          <Field data-invalid={!!errors.phoneNumber}>
            <FieldLabel htmlFor="buyer-phone">{t("checkout.buyerStep.phone")}</FieldLabel>
            <Input id="buyer-phone" type="tel" autoComplete="tel" aria-invalid={!!errors.phoneNumber} {...form.register("phoneNumber")} />
            <FieldError errors={[errors.phoneNumber]} />
          </Field>
        </FieldGroup>
        <Button type="submit" size="lg" className="w-fit" disabled={isSubmitting || gone}>
          {isSubmitting ? t("checkout.buyerStep.submitting") : t("checkout.buyerStep.submit")}
        </Button>
      </form>
      <aside className="flex h-fit flex-col gap-4 rounded-xl border bg-card p-4">
        <HoldCountdown expiresAt={hold.expiresAt} offsetMs={offsetMs} onExpire={onExpire} />
        <OrderLines lines={hold} headingId="hold-summary" />
      </aside>
    </div>
  );
}

function HoldGone({ seatsHref }: { seatsHref: string }) {
  return (
    <div role="alert" className="flex flex-col items-start gap-2 rounded-md border border-danger/30 bg-danger/10 px-3 py-2 text-sm text-danger">
      <p>{t("checkout.buyerStep.holdGone")}</p>
      <Button asChild size="sm" variant="outline">
        <Link href={seatsHref}>{t("checkout.buyerStep.backToSeats")}</Link>
      </Button>
    </div>
  );
}
