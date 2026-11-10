"use client";

import { FlaskConical } from "lucide-react";
import Link from "next/link";
import { useCallback, useEffect, useRef, useState } from "react";
import { ErrorState } from "@/components/shared/error-state";
import { Button } from "@/components/ui/button";
import { Skeleton } from "@/components/ui/skeleton";
import { navigateTo } from "@/lib/checkout/navigate";
import { completeMockSession, getMockSession } from "@/lib/checkout/orders-api";
import type { MockOutcome, MockSession } from "@/lib/checkout/types";
import { ApiError } from "@/lib/errors";
import { formatVnd } from "@/lib/format";
import { t } from "@/lib/i18n";

const OUTCOMES: MockOutcome[] = ["succeeded", "failed", "succeeded_delayed", "succeeded_twice"];

type Load = { state: "loading" } | { state: "unavailable" } | { state: "error" } | { state: "ready"; session: MockSession };

/**
 * `/mock-gateway/[sessionId]` (spec 08, dev and demo only): stands in for the provider's hosted payment page. The
 * tester picks what the "provider" does; the api sends the signed webhook(s) and the browser returns to the order,
 * exactly as a real redirect would. A production api has no such endpoints (404), which reads as "not available".
 */
export function MockGatewayView({ sessionId }: { sessionId: string }) {
  const [load, setLoad] = useState<Load>({ state: "loading" });
  const [pending, setPending] = useState<MockOutcome | null>(null);
  const [error, setError] = useState<string | null>(null);
  const running = useRef(false);

  const fetchSession = useCallback(() => {
    let active = true;
    getMockSession(sessionId).then(
      (session) => active && setLoad({ state: "ready", session }),
      (e: unknown) => active && setLoad({ state: e instanceof ApiError && e.status === 404 ? "unavailable" : "error" }),
    );
    return () => {
      active = false;
    };
  }, [sessionId]);

  useEffect(() => fetchSession(), [fetchSession]);

  const complete = async (session: MockSession, outcome: MockOutcome) => {
    if (running.current) return;
    running.current = true;
    setPending(outcome);
    setError(null);
    try {
      await completeMockSession(sessionId, outcome);
      navigateTo(`/checkout/${session.orderId}/result`);
    } catch {
      setError(t("mockGateway.completeFailed"));
      setPending(null);
      running.current = false;
    }
  };

  if (load.state === "loading") {
    return (
      <div aria-busy className="mx-auto w-full max-w-md px-4 py-10">
        <p role="status" className="sr-only">
          {t("mockGateway.loading")}
        </p>
        <Skeleton className="h-72 w-full rounded-2xl" />
      </div>
    );
  }

  if (load.state === "error") {
    return (
      <div className="mx-auto w-full max-w-md px-4 py-10">
        <ErrorState
          title={t("mockGateway.loadFailed")}
          onRetry={() => {
            setLoad({ state: "loading" });
            fetchSession();
          }}
        />
      </div>
    );
  }

  if (load.state === "unavailable") {
    return (
      <div className="mx-auto flex w-full max-w-md flex-col items-center gap-3 px-4 py-16 text-center">
        <h1 tabIndex={-1} className="text-xl font-semibold">
          {t("mockGateway.unavailable.title")}
        </h1>
        <p className="text-sm text-muted-foreground">{t("mockGateway.unavailable.body")}</p>
      </div>
    );
  }

  const { session } = load;
  const open = session.status === "Pending";
  return (
    <div className="mx-auto flex w-full max-w-md flex-col gap-6 px-4 py-10">
      <section aria-labelledby="mock-title" className="flex flex-col gap-5 rounded-2xl border bg-card p-6 shadow-sm">
        <header className="flex flex-col gap-2">
          <p className="flex items-center gap-2 rounded-md border border-warning/40 bg-warning/10 px-3 py-2 text-sm">
            <FlaskConical aria-hidden className="size-4 shrink-0" />
            {t("mockGateway.notice")}
          </p>
          <h1 id="mock-title" className="text-2xl font-bold tracking-tight">
            {t("mockGateway.title")}
          </h1>
        </header>
        <dl className="grid grid-cols-[auto_1fr] items-baseline gap-x-4 gap-y-1 text-sm">
          <dt className="text-muted-foreground">{t("mockGateway.order")}</dt>
          <dd>{session.label}</dd>
          <dt className="text-muted-foreground">{t("mockGateway.amount")}</dt>
          <dd>
            <strong className="text-2xl tabular-nums">{formatVnd(session.amount)}</strong>
          </dd>
        </dl>
        {!open ? <p className="text-sm text-muted-foreground">{t(`mockGateway.closed.${session.status}`)}</p> : null}
        {error ? (
          <p role="alert" className="rounded-md border border-danger/30 bg-danger/10 px-3 py-2 text-sm text-danger">
            {error}
          </p>
        ) : null}
        <ul className="flex flex-col gap-3">
          {OUTCOMES.map((outcome) => (
            <li key={outcome} className="flex flex-col gap-1">
              <Button variant={outcome === "succeeded" ? "default" : "outline"} size="lg" disabled={!open || pending !== null} onClick={() => void complete(session, outcome)}>
                {pending === outcome ? t("mockGateway.completing") : t(`mockGateway.outcomes.${outcome}.label`)}
              </Button>
              <p className="px-1 text-xs text-muted-foreground">{t(`mockGateway.outcomes.${outcome}.hint`)}</p>
            </li>
          ))}
        </ul>
        {!open ? (
          <Button asChild variant="link" className="w-fit">
            <Link href={`/checkout/${session.orderId}/result`}>{t("mockGateway.backToOrder")}</Link>
          </Button>
        ) : null}
      </section>
    </div>
  );
}
