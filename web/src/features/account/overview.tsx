"use client";

import { ArrowRight, CalendarDays, MailWarning, MapPin, Ticket } from "lucide-react";
import Link from "next/link";
import { usePathname, useRouter } from "next/navigation";
import { useEffect, useState, type ReactNode } from "react";
import { useAuth } from "@/components/auth/auth-provider";
import { EventPoster } from "@/components/catalog/event-poster";
import { Avatar, AvatarFallback, AvatarImage } from "@/components/ui/avatar";
import { Button } from "@/components/ui/button";
import { Skeleton } from "@/components/ui/skeleton";
import { StatusBadge } from "@/features/orders/status-badge";
import { loginRedirectUrl } from "@/lib/api/session";
import { listMyOrders } from "@/lib/checkout/orders-api";
import type { OrderSummary } from "@/lib/checkout/types";
import type { EventSummary } from "@/lib/catalog/types";
import { formatDateTime, formatVnd } from "@/lib/format";
import { t } from "@/lib/i18n.buyer";
import { getMyTickets } from "@/lib/tickets/api";
import type { TicketEventGroup } from "@/lib/tickets/types";
import { getMyFollows } from "./hub-api";
import { MiniEventCard } from "./mini-event-card";

type Loaded<T> = { state: "loading" } | { state: "ready"; value: T } | { state: "failed" };

/** One request per section, each failing on its own: a hub that half-loads is still useful. */
function useLoad<T>(load: (signal: AbortSignal) => Promise<T>): Loaded<T> {
  const [result, setResult] = useState<Loaded<T>>({ state: "loading" });
  useEffect(() => {
    const controller = new AbortController();
    load(controller.signal).then(
      (value) => !controller.signal.aborted && setResult({ state: "ready", value }),
      () => !controller.signal.aborted && setResult({ state: "failed" }),
    );
    return () => controller.abort();
    // The loaders are module functions: load once per mount.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);
  return result;
}

const loadNext = (signal: AbortSignal) => getMyTickets("upcoming", signal).then((groups) => groups[0] ?? null);
const loadOrders = (signal: AbortSignal) => listMyOrders(undefined, 3, signal).then((page) => page.items);
const loadFollows = (signal: AbortSignal) => getMyFollows(signal);

/**
 * The account hub's overview (UI refresh): who you are, the next event you hold tickets for (with the way into the ticket),
 * your latest orders and the events you follow. Every section says what to do when it is empty.
 */
export function AccountOverview() {
  const { user, status } = useAuth();
  const router = useRouter();
  const pathname = usePathname();
  // proxy.ts already redirects when the hint cookie is missing; this covers a hint that outlived its session.
  useEffect(() => {
    if (status === "anonymous") router.replace(loginRedirectUrl(pathname));
  }, [status, router, pathname]);
  const next = useLoad<TicketEventGroup | null>(loadNext);
  const orders = useLoad<OrderSummary[]>(loadOrders);
  const follows = useLoad<EventSummary[]>(loadFollows);

  if (!user) return <Skeleton className="h-40 w-full rounded-2xl" />;

  return (
    <div className="flex flex-col gap-8">
      <header className="flex items-center gap-4">
        <Avatar className="size-14 ring-2 ring-primary/20">
          {user.avatarUrl ? <AvatarImage src={user.avatarUrl} alt="" /> : null}
          <AvatarFallback className="text-lg">{user.fullName.charAt(0).toUpperCase()}</AvatarFallback>
        </Avatar>
        <div className="flex min-w-0 flex-col">
          <h1 className="truncate text-2xl font-bold tracking-tight">{t("accountHub.greeting", "vi", { name: user.fullName })}</h1>
          <p className="truncate text-sm text-muted-foreground">{user.email}</p>
        </div>
      </header>

      {!user.emailConfirmed ? (
        <p role="status" className="flex items-start gap-2 rounded-xl border border-warning/40 bg-warning/10 px-4 py-3 text-sm">
          <MailWarning aria-hidden className="mt-0.5 size-4 shrink-0" />
          {t("account.emailUnconfirmed")}
        </p>
      ) : null}

      <Block id="next-event" title={t("accountHub.next.title")}>
        {next.state === "loading" ? (
          <Skeleton className="h-40 w-full rounded-2xl" />
        ) : next.state === "ready" && next.value ? (
          <NextEvent group={next.value} />
        ) : (
          <Empty text={t("accountHub.next.empty")} action={<Link href="/events">{t("accountHub.explore")}</Link>} />
        )}
      </Block>

      <Block id="recent-orders" title={t("accountHub.orders.title")} action={<SeeAll href="/account/orders" />}>
        {orders.state === "loading" ? (
          <Skeleton className="h-28 w-full rounded-2xl" />
        ) : orders.state === "ready" && orders.value.length > 0 ? (
          <ul className="divide-y rounded-2xl border bg-card">
            {orders.value.map((order) => (
              <li key={order.id} className="flex items-center justify-between gap-3 px-4 py-3">
                <div className="flex min-w-0 flex-col">
                  <Link href={`/account/orders/${order.id}`} className="truncate font-medium underline-offset-4 hover:underline">
                    {order.eventTitle || t("orders.eventFallback")}
                  </Link>
                  <span className="text-xs text-muted-foreground">{formatDateTime(order.createdAt)}</span>
                </div>
                <div className="flex shrink-0 items-center gap-3">
                  <span className="text-sm font-semibold tabular-nums">{formatVnd(order.total)}</span>
                  <StatusBadge status={order.status} />
                </div>
              </li>
            ))}
          </ul>
        ) : (
          <Empty text={t("accountHub.orders.empty")} />
        )}
      </Block>

      <Block id="follows" title={t("accountHub.follows.title")}>
        {follows.state === "loading" ? (
          <Skeleton className="h-32 w-full rounded-2xl" />
        ) : follows.state === "ready" && follows.value.length > 0 ? (
          <ul className="grid grid-cols-2 gap-3 sm:grid-cols-3 lg:grid-cols-4">
            {follows.value.map((event) => (
              <li key={event.slug}>
                <MiniEventCard event={event} />
              </li>
            ))}
          </ul>
        ) : (
          <Empty text={t("accountHub.follows.empty")} />
        )}
      </Block>
    </div>
  );
}

function NextEvent({ group }: { group: TicketEventGroup }) {
  const first = group.tickets[0];
  return (
    <div className="grid overflow-hidden rounded-2xl border bg-card sm:grid-cols-[minmax(0,2fr)_minmax(0,3fr)]">
      <div className="relative aspect-video sm:aspect-auto">
        <EventPoster slug={group.slug} category="" title={group.title} coverImageUrl={group.coverImageUrl} sizes="(min-width: 640px) 320px, 100vw" />
      </div>
      <div className="flex flex-col gap-3 p-5">
        <h3 className="text-lg font-semibold leading-snug">{group.title}</h3>
        <p className="flex items-center gap-2 text-sm text-muted-foreground">
          <CalendarDays aria-hidden className="size-4" />
          {formatDateTime(group.startsAt)}
        </p>
        <p className="flex items-center gap-2 text-sm text-muted-foreground">
          <MapPin aria-hidden className="size-4" />
          {group.venueName}, {group.city}
        </p>
        <p className="text-sm">{t("accountHub.next.count", "vi", { count: group.tickets.length })}</p>
        {first ? (
          <Button asChild className="mt-auto self-start">
            <Link href={`/tickets/${first.id}`}>
              <Ticket aria-hidden />
              {t("accountHub.next.open")}
            </Link>
          </Button>
        ) : null}
      </div>
    </div>
  );
}

function Block({ id, title, action, children }: { id: string; title: string; action?: ReactNode; children: ReactNode }) {
  return (
    <section aria-labelledby={id} className="flex flex-col gap-3">
      <div className="flex items-end justify-between gap-3">
        <h2 id={id} className="text-lg font-semibold tracking-tight">
          {title}
        </h2>
        {action}
      </div>
      {children}
    </section>
  );
}

function SeeAll({ href }: { href: string }) {
  return (
    <Link href={href} className="flex items-center gap-1 text-sm font-medium text-primary hover:underline">
      {t("accountHub.seeAll")}
      <ArrowRight aria-hidden className="size-4" />
    </Link>
  );
}

function Empty({ text, action }: { text: string; action?: ReactNode }) {
  return (
    <div className="flex flex-col items-start gap-3 rounded-2xl border border-dashed p-5 text-sm text-muted-foreground">
      <p>{text}</p>
      {action ? (
        <Button asChild variant="outline" size="sm">
          {action}
        </Button>
      ) : null}
    </div>
  );
}
