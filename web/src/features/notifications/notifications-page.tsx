"use client";

import { BellOff } from "lucide-react";
import { useRouter } from "next/navigation";
import { useEffect, useRef, useState } from "react";
import { useAuth } from "@/components/auth/auth-provider";
import { EmptyState } from "@/components/shared/empty-state";
import { ErrorState } from "@/components/shared/error-state";
import { Button } from "@/components/ui/button";
import { Skeleton } from "@/components/ui/skeleton";
import { loginRedirectUrl } from "@/lib/api/session";
import { t } from "@/lib/i18n";
import { unreadCountOf } from "@/lib/notifications/cache";
import type { NotificationItem, NotificationPage } from "@/lib/notifications/types";
import { useMarkAllRead, useNotifications } from "@/lib/notifications/use-notifications";
import { NotificationList, useOpenNotification } from "./notification-list";

export const NOTIFICATIONS_HREF = "/account/notifications";

/** The pages in order, each notification once: a push prepends to the first page while later pages are already loaded. */
function flatten(pages: NotificationPage[] | undefined): NotificationItem[] {
  return [...new Map((pages ?? []).flatMap((page) => page.items).map((item) => [item.id, item])).values()];
}

/**
 * `/account/notifications` (spec 09): the whole inbox, newest first, a page at a time (keyset: the api's `nextCursor`).
 * Same cache as the bell, so a read or a live push shows in both. proxy.ts sends signed-out visitors to login; this covers
 * a session hint that outlived its session.
 */
export function NotificationsPage() {
  const router = useRouter();
  const { status } = useAuth();
  const heading = useRef<HTMLHeadingElement>(null);
  const [announcement, setAnnouncement] = useState("");
  const query = useNotifications();
  const markAll = useMarkAllRead();
  const openItem = useOpenNotification();
  const rows = flatten(query.data?.pages);
  const settled = query.isSuccess || query.isError;
  const unread = unreadCountOf(query.data);

  useEffect(() => {
    if (status === "anonymous") router.replace(loginRedirectUrl(NOTIFICATIONS_HREF));
  }, [status, router]);

  // The heading takes focus once, when the first answer is in (a route change should land on the new content).
  const focused = useRef(false);
  useEffect(() => {
    if (settled && !focused.current) {
      focused.current = true;
      heading.current?.focus();
    }
  }, [settled]);

  async function loadMore() {
    const before = rows.length;
    const result = await query.fetchNextPage();
    const next = flatten(result.data?.pages);
    if (result.isError || next.length <= before) return;
    setAnnouncement(t("notifications.loadedMore", "vi", { count: next.length - before, total: next.length }));
  }

  return (
    <div className="mx-auto flex w-full max-w-3xl flex-col gap-6 px-4 py-10">
      <header className="flex flex-col gap-4 border-b pb-6 sm:flex-row sm:items-end sm:justify-between">
        <div className="flex flex-col gap-1">
          <h1 ref={heading} tabIndex={-1} className="text-2xl font-bold tracking-tight outline-none sm:text-3xl">
            {t("notifications.title")}
          </h1>
          <p className="text-muted-foreground">{t("notifications.description")}</p>
        </div>
        {rows.length > 0 ? (
          <Button variant="outline" disabled={unread === 0 || markAll.isPending} onClick={() => markAll.mutate()}>
            {t("notifications.markAllRead")}
          </Button>
        ) : null}
      </header>
      {/* The one live region: the loading message first, then how many a "Tải thêm" added. */}
      <p role="status" className="sr-only">
        {query.isPending ? t("notifications.loading") : announcement}
      </p>
      {query.isError && rows.length === 0 ? (
        <ErrorState title={t("notifications.loadFailed")} onRetry={() => void query.refetch()} />
      ) : query.isPending ? (
        <ListSkeleton />
      ) : rows.length === 0 ? (
        <EmptyState icon={BellOff} title={t("notifications.empty.title")} description={t("notifications.empty.description")} />
      ) : (
        <>
          <NotificationList items={rows} onOpen={openItem} label={t("notifications.list")} className="overflow-hidden rounded-xl border bg-card" />
          {query.isFetchNextPageError ? (
            <p role="alert" className="text-center text-sm text-danger">
              {t("notifications.loadMoreFailed")}
            </p>
          ) : null}
          {query.hasNextPage ? (
            <Button variant="outline" className="self-center" disabled={query.isFetchingNextPage} onClick={() => void loadMore()}>
              {query.isFetchingNextPage ? t("notifications.loadingMore") : t("notifications.loadMore")}
            </Button>
          ) : null}
        </>
      )}
    </div>
  );
}

function ListSkeleton() {
  return (
    <div aria-busy className="flex flex-col gap-3">
      {[0, 1, 2].map((i) => (
        <Skeleton key={i} className="h-20 w-full rounded-xl" />
      ))}
    </div>
  );
}
