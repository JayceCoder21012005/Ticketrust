"use client";

import { ArrowLeft, CalendarDays, MapPin, Sun, TicketX, WifiOff } from "lucide-react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { useEffect, useEffectEvent, useRef, useState, type KeyboardEvent, type PointerEvent, type ReactNode } from "react";
import { useAuth } from "@/components/auth/auth-provider";
import { EmptyState } from "@/components/shared/empty-state";
import { ErrorState } from "@/components/shared/error-state";
import { Button } from "@/components/ui/button";
import { Skeleton } from "@/components/ui/skeleton";
import { useWakeLock } from "@/features/queue/use-queue-effects";
import { loginRedirectUrl } from "@/lib/api/session";
import { ApiError, NetworkError, toFriendlyMessage } from "@/lib/errors";
import { formatEventDate, formatTimeRange } from "@/lib/format";
import { t } from "@/lib/i18n";
import { keyCache, ticketCache } from "@/lib/tickets/key-cache";
import { registerTicketWorker } from "@/lib/tickets/register-sw";
import type { TicketDetail } from "@/lib/tickets/types";
import { useTicket } from "@/lib/tickets/use-tickets";
import { AddToCalendar } from "./add-to-calendar";
import { RotatingQr } from "./rotating-qr";
import { SeatInfo } from "./seat-info";
import { siblingsOf, SiblingNav, walletHref } from "./sibling-nav";
import { StatusOverlay, type OverlayKind } from "./status-overlay";
import { useQrKey, type QrKeyState } from "./use-qr-key";
import { useServerOffset } from "./use-server-offset";

const TICKETS_HREF = "/account/tickets";

/** A horizontal drag at least this long (and mostly horizontal) moves to the next or previous ticket. */
const SWIPE_PX = 60;

/** `setTimeout` takes at most 2^31 − 1 ms (about 24.8 days); a longer wait is re-aimed when it fires. */
const MAX_TIMEOUT_MS = 2 ** 31 - 1;

/**
 * What the QR slot shows for the ticket as loaded, at `serverNowMs`: a code only for a valid ticket of an event still
 * to come or running.
 */
function displayOf(ticket: TicketDetail, serverNowMs: number): "qr" | OverlayKind {
  if (ticket.status === "Listed") return "listed";
  if (ticket.status === "CheckedIn") return "checkedIn";
  if (ticket.status === "Voided" || ticket.event.status === "Cancelled") return "voided";
  if (ticket.event.status === "Ended" || Date.parse(ticket.event.endsAt) <= serverNowMs) return "ended";
  return "qr";
}

type CachedRead = { ticketId: string; ticket: TicketDetail | null };

/**
 * The ticket detail saved on an earlier online visit, read when the api cannot be reached (undefined while reading).
 * Only its owner gets it: a signed-in user claims the cache first (another user's is wiped); without a session only
 * the cache of the last user signed in on this device is served.
 */
function useCachedTicket(ticketId: string, active: boolean, userId: string | null): TicketDetail | null | undefined {
  const [read, setRead] = useState<CachedRead | null>(null);

  useEffect(() => {
    if (!active) return;
    let live = true;
    ticketCache.read(ticketId, userId).then((ticket) => live && setRead({ ticketId, ticket }));
    return () => {
      live = false;
    };
  }, [ticketId, active, userId]);

  return active && read?.ticketId === ticketId ? read.ticket : undefined;
}

/**
 * `/tickets/[id]` (spec 10): the ticket "wallet" screen. proxy.ts sends visitors without a session hint to login; this
 * covers a hint that outlived its session and a 401 the silent refresh could not fix. The api answers 404 for an
 * unknown ticket and for someone else's alike.
 *
 * Offline (the api cannot be reached: the session could not be restored, or the ticket request got no answer at all)
 * the ticket saved on an earlier online visit is shown, with its cached key; a ticket never opened online here says so.
 */
export function WalletView({ ticketId }: { ticketId: string }) {
  const router = useRouter();
  const { status, offline, user } = useAuth();
  const userId = user?.id ?? null;
  const query = useTicket(ticketId);
  const expired = query.error instanceof ApiError && query.error.status === 401;
  const unreachable = !query.data && (offline || query.error instanceof NetworkError);
  const cached = useCachedTicket(ticketId, unreachable, userId);

  useEffect(() => {
    if ((status === "anonymous" && !offline) || expired) router.replace(loginRedirectUrl(walletHref(ticketId)));
  }, [status, offline, expired, ticketId, router]);

  // The service worker keeps this page's shell for the next visit without signal.
  useEffect(() => registerTicketWorker(), []);

  // A loaded ticket stays on screen when a later refetch fails.
  if (query.data) return <Wallet ticket={query.data} fromNetwork onStale={() => void query.refetch()} />;
  if (unreachable && cached) return <Wallet ticket={cached} fromNetwork={false} onStale={() => void query.refetch()} />;
  if (unreachable && cached === null) {
    return (
      <Frame>
        <BackLink />
        <ErrorState
          title={t("tickets.wallet.offlineUnavailable.title")}
          description={t("tickets.wallet.offlineUnavailable.description")}
          // Without a session (it could not be restored offline) only a reload tries everything again.
          onRetry={offline ? () => window.location.reload() : () => void query.refetch()}
        />
      </Frame>
    );
  }
  if (query.isError && !expired && !unreachable) {
    return query.error instanceof ApiError && query.error.status === 404 ? (
      <NotFound />
    ) : (
      <Frame>
        <ErrorState title={t("tickets.wallet.loadFailed")} description={toFriendlyMessage(query.error)} onRetry={() => void query.refetch()} />
      </Frame>
    );
  }

  return <WalletSkeleton />;
}

function Frame({ children }: { children: ReactNode }) {
  return <div className="mx-auto flex w-full max-w-md flex-col gap-5 px-4 py-6">{children}</div>;
}

function BackLink() {
  return (
    <Button asChild variant="ghost" size="sm" className="-ml-2 self-start">
      <Link href={TICKETS_HREF}>
        <ArrowLeft aria-hidden />
        {t("tickets.wallet.back")}
      </Link>
    </Button>
  );
}

function NotFound() {
  return (
    <Frame>
      <EmptyState
        icon={TicketX}
        title={t("tickets.wallet.notFound.title")}
        description={t("tickets.wallet.notFound.description")}
        action={
          <Button asChild>
            <Link href={TICKETS_HREF}>{t("tickets.wallet.notFound.action")}</Link>
          </Button>
        }
      />
    </Frame>
  );
}

type WalletProps = {
  ticket: TicketDetail;
  /** The api's answer (true) or the copy saved for offline use (false). */
  fromNetwork: boolean;
  /** Reloads the ticket: the api refused a key, so the status we have is out of date. */
  onStale: () => void;
};

function Wallet({ ticket, fromNetwork, onStale }: WalletProps) {
  const router = useRouter();
  const { user } = useAuth();
  const userId = user?.id ?? null;
  const { offsetMs, settled: clockReady } = useServerOffset();
  // The device time the display was last evaluated at: on mount, then when the event ends (see below).
  const [checkedAt, setCheckedAt] = useState(() => Date.now());
  const display = displayOf(ticket, checkedAt + offsetMs);
  const key = useQrKey(ticket.id, display === "qr", ticket.event.endsAt, offsetMs, userId);
  const heading = useRef<HTMLHeadingElement>(null);
  const swipe = useRef<{ id: number; x: number; y: number } | null>(null);
  const { previous, next } = siblingsOf(ticket.orderTicketIds, ticket.id);
  const refused = key.status === "refused" ? key.code : null;
  const endsAt = ticket.event.endsAt;

  // The new ticket's content after a route change (or the first load) gets focus, so the arrow keys work at once.
  useEffect(() => heading.current?.focus(), []);
  // Keeps the screen on while the code is shown at the gate (where supported; released on leave).
  useWakeLock(key.status === "ready");

  // The code disappears when the event ends on the server clock, even with the screen left open.
  useEffect(() => {
    if (display !== "qr") return;
    const left = Date.parse(endsAt) - (Date.now() + offsetMs);
    const timer = setTimeout(() => setCheckedAt(Date.now()), Math.min(Math.max(0, left), MAX_TIMEOUT_MS));
    return () => clearTimeout(timer);
  }, [display, endsAt, offsetMs, checkedAt]);

  // A ticket the api just gave a key for is saved for offline use (never one it refused: that answer deletes it), once
  // the cache is marked as this user's (what another user left is wiped first).
  const fresh = key.status === "ready" && key.source === "network";
  useEffect(() => {
    if (!fresh || !userId) return;
    void keyCache.claim(userId).then(() => ticketCache.put(ticket));
  }, [fresh, userId, ticket]);

  // The api says this ticket cannot show a code (listed, checked in, voided, its event cancelled or over): no key is
  // asked for, so the 409 path never runs. Its cached key goes now, and the stale "valid" detail is replaced by the
  // real one, so offline the same overlay shows and `purgeEnded` sees the real status (it drops it once over).
  useEffect(() => {
    if (!fromNetwork || display === "qr") return;
    void keyCache.delete(ticket.id);
    if (userId) void keyCache.claim(userId).then(() => ticketCache.put(ticket));
    else void ticketCache.delete(ticket.id);
  }, [fromNetwork, display, ticket, userId]);

  // Keys and tickets of events that are over leave the offline cache.
  const purge = useEffectEvent(() => void keyCache.purgeEnded(Date.now() + offsetMs));
  useEffect(() => purge(), []);

  const reload = useEffectEvent(onStale);
  useEffect(() => {
    if (refused === "not_displayable") reload();
  }, [refused]);

  if (refused === "not_found") return <NotFound />;

  const go = (id: string | null) => id && router.replace(walletHref(id), { scroll: false });

  const onKeyDown = (event: KeyboardEvent<HTMLDivElement>) => {
    if (event.defaultPrevented || event.altKey || event.ctrlKey || event.metaKey || event.shiftKey) return;
    if (event.key === "ArrowLeft" && previous) {
      event.preventDefault();
      go(previous);
    } else if (event.key === "ArrowRight" && next) {
      event.preventDefault();
      go(next);
    }
  };

  const onPointerDown = (event: PointerEvent<HTMLDivElement>) => {
    swipe.current = event.pointerType === "mouse" ? null : { id: event.pointerId, x: event.clientX, y: event.clientY };
  };
  const onPointerUp = (event: PointerEvent<HTMLDivElement>) => {
    const start = swipe.current;
    swipe.current = null;
    if (!start || start.id !== event.pointerId) return;
    const dx = event.clientX - start.x;
    const dy = event.clientY - start.y;
    if (Math.abs(dx) < SWIPE_PX || Math.abs(dx) < 2 * Math.abs(dy)) return;
    go(dx < 0 ? next : previous);
  };

  const showsCode = display === "qr" && key.status === "ready";
  const fromCache = showsCode && key.source === "cache";

  return (
    <div
      data-testid="wallet"
      onKeyDown={onKeyDown}
      onPointerDown={onPointerDown}
      onPointerUp={onPointerUp}
      onPointerCancel={() => (swipe.current = null)}
      className="mx-auto flex w-full max-w-md touch-pan-y flex-col gap-5 px-4 py-6"
    >
      <BackLink />
      <header className="flex flex-col gap-2">
        <h1 ref={heading} tabIndex={-1} className="break-words text-2xl font-bold tracking-tight outline-none">
          {ticket.event.title}
        </h1>
        <p className="flex items-start gap-1.5 text-sm text-muted-foreground">
          <CalendarDays aria-hidden className="mt-0.5 size-4 shrink-0" />
          <span>{`${formatEventDate(ticket.event.startsAt)} · ${formatTimeRange(ticket.event.startsAt, ticket.event.endsAt)}`}</span>
        </p>
        <p className="flex items-start gap-1.5 text-sm text-muted-foreground">
          <MapPin aria-hidden className="mt-0.5 size-4 shrink-0" />
          <span className="min-w-0 break-words">{`${ticket.event.venueName}, ${ticket.event.city}`}</span>
        </p>
      </header>
      <SiblingNav ids={ticket.orderTicketIds} currentId={ticket.id} />
      <section aria-label={t("tickets.wallet.qrSection")} className="flex flex-col items-center">
        {display === "qr" ? <CodeSlot ticket={ticket} keyState={key} offsetMs={offsetMs} clockReady={clockReady} /> : <StatusOverlay kind={display} />}
        {/* One polite live region, filled once when the cached key is in use (the ticking code never touches it). */}
        <p role="status" className={fromCache ? "mt-3 flex items-center gap-1.5 rounded-full bg-muted px-3 py-1 text-xs font-medium" : "sr-only"}>
          {fromCache ? (
            <>
              <WifiOff aria-hidden className="size-3.5 shrink-0" />
              {t("tickets.wallet.offlineHint")}
            </>
          ) : null}
        </p>
      </section>
      {showsCode ? (
        <p className="flex items-start gap-2 rounded-xl bg-muted/60 px-4 py-3 text-sm">
          <Sun aria-hidden className="mt-0.5 size-4 shrink-0 text-warning" />
          <span>{t("tickets.wallet.brightness")}</span>
        </p>
      ) : null}
      <SeatInfo seat={ticket.seat} tierName={ticket.tierName} holderName={ticket.holderName} />
      <AddToCalendar ticket={ticket} />
    </div>
  );
}

const failureCopy = (failure: string) => (failure === "network" || failure === "busy" ? failure : "other");

type CodeSlotProps = {
  ticket: TicketDetail;
  keyState: QrKeyState & { retry: () => void };
  offsetMs: number;
  /** False while the server clock is still being asked for (1.5 s at most): the code waits rather than jump. */
  clockReady: boolean;
};

/** The QR slot of a displayable ticket, through the life of its `qr-key` request. */
function CodeSlot({ ticket, keyState, offsetMs, clockReady }: CodeSlotProps) {
  switch (keyState.status) {
    case "ready":
      return clockReady ? <RotatingQr ticketId={ticket.id} secret={keyState.secret} offsetMs={offsetMs} /> : <QrPlaceholder />;
    case "refused":
      return <StatusOverlay kind="notDisplayable" />;
    case "rateLimited":
      return (
        <div className="flex aspect-square w-full max-w-80 flex-col items-center justify-center gap-2 rounded-2xl border-2 border-dashed bg-muted/40 p-6 text-center">
          <h2 className="text-lg font-semibold">{t("tickets.wallet.rateLimited.title")}</h2>
          {/* The countdown is not a live region; the status below is read once. */}
          <p className="text-2xl font-bold tabular-nums">{t("tickets.wallet.rateLimited.retryIn", "vi", { seconds: keyState.retryAfter })}</p>
          <p role="status" className="text-sm text-muted-foreground">
            {t("tickets.wallet.rateLimited.announce")}
          </p>
        </div>
      );
    case "error":
      return (
        <ErrorState
          className="w-full"
          title={t("tickets.wallet.keyFailed.title")}
          description={t(`tickets.wallet.keyFailed.${failureCopy(keyState.failure)}`)}
          onRetry={keyState.retry}
        />
      );
    default:
      return <QrPlaceholder />;
  }
}

/** The QR slot while the key or the server clock is on its way. */
function QrPlaceholder() {
  return (
    <div className="aspect-square w-full max-w-80">
      <Skeleton className="size-full rounded-2xl" />
      <p role="status" className="sr-only">
        {t("tickets.wallet.qrLoading")}
      </p>
    </div>
  );
}

export function WalletSkeleton() {
  return (
    <Frame>
      <p role="status" className="sr-only">
        {t("tickets.wallet.loading")}
      </p>
      <div aria-busy className="flex flex-col items-center gap-5">
        <Skeleton className="h-8 w-3/4 self-start" />
        <Skeleton className="h-4 w-1/2 self-start" />
        <Skeleton className="aspect-square w-full max-w-80 rounded-2xl" />
        <div className="grid w-full grid-cols-3 gap-2">
          <Skeleton className="h-20" />
          <Skeleton className="h-20" />
          <Skeleton className="h-20" />
        </div>
      </div>
    </Frame>
  );
}
