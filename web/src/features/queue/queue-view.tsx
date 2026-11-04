"use client";

import Link from "next/link";
import { useRouter } from "next/navigation";
import { useEffect, useRef, useState } from "react";
import { EventPoster } from "@/components/catalog/event-poster";
import { ErrorState } from "@/components/shared/error-state";
import { Button } from "@/components/ui/button";
import { Skeleton } from "@/components/ui/skeleton";
import { ApiError, toFriendlyMessage } from "@/lib/errors";
import { t } from "@/lib/i18n";
import { useQueue } from "@/lib/queue/use-queue";
import { AccessCodeForm } from "./access-code-form";
import { AdmittedState } from "./admitted-state";
import { ClosedState } from "./closed-state";
import { PreState } from "./pre-state";
import { useAdmittedAlerts, useTabTitle, useWakeLock } from "./use-queue-effects";
import { WaitingState } from "./waiting-state";

type QueueEvent = { id: string; slug: string; title: string; category: string; coverImageUrl: string | null };

function notificationPermission(): NotificationPermission | "unsupported" {
  return typeof Notification === "undefined" ? "unsupported" : Notification.permission;
}

/**
 * The buyer's waiting room (spec 06): a calm card over the blurred cover. It joins on arrival (the api makes that
 * idempotent, so a reload or a second tab changes nothing) and then only renders what the server says.
 */
export function QueueView({ event }: { event: QueueEvent }) {
  const router = useRouter();
  const queue = useQueue(event.id, event.slug);
  const { join, leave, status, error, phase } = queue;
  const joined = useRef(false);
  const [codeTried, setCodeTried] = useState(false);
  // Only read after the first client render: the waiting card that shows it is never part of the server HTML.
  const [notification, setNotification] = useState<NotificationPermission | "unsupported">(notificationPermission);

  useEffect(() => {
    if (!joined.current) {
      joined.current = true;
      void join();
    }
  }, [join]);

  const waiting = status?.status === "Waiting";
  useTabTitle(waiting ? status.position : null);
  useAdmittedAlerts({ admitted: status?.status === "Admitted", eventTitle: event.title });
  useWakeLock(phase === "active" && (waiting || status?.status === "Pre"));

  const submitCode = (code: string) => {
    setCodeTried(true);
    void join(code);
  };

  const leaveRoom = async () => {
    await leave().catch(() => {});
    router.push(`/events/${event.slug}`);
  };

  const enableNotifications = () => {
    void Notification.requestPermission().then(setNotification);
  };

  return (
    <div className="relative isolate flex min-h-[calc(100svh-8rem)] flex-1 items-center justify-center overflow-hidden px-4 py-10">
      <div aria-hidden className="absolute inset-0 -z-10 scale-110 opacity-60 blur-2xl">
        <EventPoster slug={event.slug} category={event.category} title={event.title} coverImageUrl={event.coverImageUrl} sizes="100vw" />
      </div>
      <div aria-hidden className="absolute inset-0 -z-10 bg-background/60" />

      <section className="flex w-full max-w-md flex-col gap-6 rounded-2xl bg-card p-6 shadow-xl ring-1 ring-foreground/10 sm:p-8">
        <header className="flex flex-col items-center gap-1 text-center">
          <p className="text-xs font-medium uppercase tracking-wide text-muted-foreground">{t("queue.title")}</p>
          <h1 className="text-balance text-base font-semibold">{event.title}</h1>
        </header>

        {/* Screen readers hear the state change once, not every poll. */}
        <p role="status" className="sr-only">
          {status ? t(`queue.${stateKey(status.status)}.title`) : ""}
        </p>

        {renderBody()}

        {status && ["Pre", "Waiting", "Admitted"].includes(status.status) ? (
          <Button variant="ghost" size="sm" className="self-center text-muted-foreground" onClick={leaveRoom}>
            {t("queue.leave")}
          </Button>
        ) : null}
      </section>
    </div>
  );

  function renderBody() {
    if (status && phase !== "failed") {
      switch (status.status) {
        case "Pre":
          return <PreState status={status} />;
        case "Waiting":
          return (
            <WaitingState
              status={status}
              startPosition={queue.startPosition}
              connectionIssues={queue.connectionIssues}
              notification={notification}
              onEnableNotification={enableNotifications}
            />
          );
        case "Admitted":
          return <AdmittedState status={status} slug={event.slug} />;
        default:
          return <ClosedState kind={status.status} slug={event.slug} onRejoin={() => void join()} />;
      }
    }

    if (phase === "failed" && error) {
      return renderFailure(error);
    }

    return (
      <div className="flex flex-col items-center gap-4" aria-busy="true">
        <Skeleton className="h-8 w-8 rounded-full" />
        <p className="text-sm text-muted-foreground">{t("queue.joining")}</p>
        <Skeleton className="h-2 w-full" />
      </div>
    );
  }

  function renderFailure(failure: ApiError) {
    switch (failure.code) {
      case "queue.invalid_access_code":
        return <AccessCodeForm onSubmit={submitCode} error={codeTried ? toFriendlyMessage(failure) : undefined} />;
      case "queue.closed":
        return <ClosedState kind="Closed" slug={event.slug} onRejoin={() => {}} />;
      default:
        return (
          <div className="flex flex-col items-center gap-3">
            <ErrorState title={t("queue.failed.title")} description={toFriendlyMessage(failure)} onRetry={() => void join()} />
            <Button asChild variant="ghost" size="sm">
              <Link href={`/events/${event.slug}`}>{t("queue.failed.back")}</Link>
            </Button>
          </div>
        );
    }
  }
}

function stateKey(status: string): string {
  return status === "Waiting" ? "waiting" : status === "Pre" ? "pre" : status === "Admitted" ? "admitted" : status === "AllHeld" ? "allHeld" : status === "SoldOut" ? "soldOut" : status === "Expired" ? "expired" : "closed";
}
