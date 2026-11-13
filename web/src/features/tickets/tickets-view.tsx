"use client";

import { Ticket } from "lucide-react";
import Link from "next/link";
import { useRouter, useSearchParams } from "next/navigation";
import { useEffect, useRef } from "react";
import { useAuth } from "@/components/auth/auth-provider";
import { EmptyState } from "@/components/shared/empty-state";
import { ErrorState } from "@/components/shared/error-state";
import { Button } from "@/components/ui/button";
import { Skeleton } from "@/components/ui/skeleton";
import { loginRedirectUrl } from "@/lib/api/session";
import { ApiError, toFriendlyMessage } from "@/lib/errors";
import { t } from "@/lib/i18n";
import { keyCache } from "@/lib/tickets/key-cache";
import { registerTicketWorker } from "@/lib/tickets/register-sw";
import type { TicketScope } from "@/lib/tickets/types";
import { useMyTickets } from "@/lib/tickets/use-tickets";
import { TicketCard } from "./ticket-card";
import { parseScope, TicketsTabs } from "./tickets-tabs";

export const TICKETS_HREF = "/account/tickets";

/** The address of a tab: the default one has none, so the plain page url stays the canonical one. */
const hrefOf = (scope: TicketScope) => (scope === "upcoming" ? TICKETS_HREF : `${TICKETS_HREF}?scope=${scope}`);

/**
 * `/account/tickets` (spec 10): the buyer's tickets grouped by event, "Sắp diễn ra" and "Đã qua". The tab is the
 * `?scope=` of the url (it survives a reload and can be linked). proxy.ts sends signed-out visitors to login; this
 * covers a session hint that outlived its session, and a 401 that the silent refresh could not fix.
 */
export function TicketsView() {
  const router = useRouter();
  const scope = parseScope(useSearchParams().get("scope"));
  const { status, user } = useAuth();
  const userId = user?.id ?? null;
  const heading = useRef<HTMLHeadingElement>(null);
  const query = useMyTickets(scope);
  const settled = query.isSuccess || query.isError;
  const expired = query.error instanceof ApiError && query.error.status === 401;

  useEffect(() => {
    if (status === "anonymous" || expired) router.replace(loginRedirectUrl(hrefOf(scope)));
  }, [status, expired, scope, router]);

  // The wallet's service worker is registered from here too, so the shell is ready before the first ticket opens.
  useEffect(() => registerTicketWorker(), []);

  // Each time the list loads, the offline cache drops what belongs to events that are over (and to another user).
  const loaded = query.isSuccess ? query.dataUpdatedAt : 0;
  useEffect(() => {
    if (!loaded || !userId) return;
    void keyCache.claim(userId).then(() => keyCache.purgeEnded(Date.now()));
  }, [loaded, userId]);

  // The heading takes focus once, when the first answer is in (a route change should land on the new content).
  const focused = useRef(false);
  useEffect(() => {
    if (settled && !focused.current) {
      focused.current = true;
      heading.current?.focus();
    }
  }, [settled]);

  // A tab is a replace, not a push: the back button leaves the page instead of walking through the tabs.
  const choose = (next: TicketScope) => router.replace(hrefOf(next), { scroll: false });

  return (
    <div className="mx-auto flex w-full max-w-3xl flex-col gap-6 px-4 py-10">
      <header className="flex flex-col gap-1 border-b pb-6">
        <h1 ref={heading} tabIndex={-1} className="text-2xl font-bold tracking-tight outline-none sm:text-3xl">
          {t("tickets.title")}
        </h1>
        <p className="text-muted-foreground">{t("tickets.description")}</p>
      </header>
      {/* The one live region: the loading message, then what the tab shows (it changes when the tab does). */}
      <p role="status" className="sr-only">
        {query.isPending ? t("tickets.loading") : query.isSuccess ? t("tickets.shown", "vi", { tab: t(`tickets.tabs.${scope}`), count: query.data.length }) : ""}
      </p>
      <TicketsTabs scope={scope} onScopeChange={choose}>
        {expired ? (
          <p role="alert" className="rounded-xl border border-danger/30 bg-danger/5 px-6 py-12 text-center text-sm">
            {t("tickets.sessionExpired")}
          </p>
        ) : query.isError ? (
          <ErrorState title={t("tickets.loadFailed")} description={toFriendlyMessage(query.error)} onRetry={() => void query.refetch()} />
        ) : query.isPending ? (
          <TicketsSkeleton />
        ) : query.data.length === 0 ? (
          <EmptyState
            icon={Ticket}
            title={t(`tickets.empty.${scope}.title`)}
            description={t(`tickets.empty.${scope}.description`)}
            action={
              <Button asChild>
                <Link href="/events">{t("tickets.empty.action")}</Link>
              </Button>
            }
          />
        ) : (
          <ul aria-label={t("tickets.list")} className="flex flex-col gap-4">
            {query.data.map((group, index) => (
              <TicketCard key={group.eventId} group={group} scope={scope} priority={index === 0} />
            ))}
          </ul>
        )}
      </TicketsTabs>
    </div>
  );
}

export function TicketsSkeleton() {
  return (
    <div aria-busy className="flex flex-col gap-4">
      {[0, 1, 2].map((i) => (
        <Skeleton key={i} className="h-64 w-full rounded-xl sm:h-36" />
      ))}
    </div>
  );
}
