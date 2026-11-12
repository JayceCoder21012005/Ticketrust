"use client";

import { QueryClient, QueryClientContext, QueryClientProvider } from "@tanstack/react-query";
import Link from "next/link";
import { use, useEffect, useState, useSyncExternalStore, type ReactNode } from "react";
import { Button } from "@/components/ui/button";
import { Popover, PopoverContent, PopoverTrigger } from "@/components/ui/popover";
import { Skeleton } from "@/components/ui/skeleton";
import { getSession, refreshAccessToken, subscribe } from "@/lib/api/session";
import { useDropCacheOnUserChange } from "@/lib/api/use-drop-cache-on-user-change";
import { t } from "@/lib/i18n";
import { unreadCountOf } from "@/lib/notifications/cache";
import { PAGE_SIZE } from "@/lib/notifications/keys";
import { useMarkAllRead, useNotifications } from "@/lib/notifications/use-notifications";
import { BellButton } from "./bell-button";
import { NotificationList, useOpenNotification } from "./notification-list";

/**
 * Public pages have no `AppProviders` (their JS is budgeted), so a signed-in visitor there gets a query client of the
 * bell's own, dropped with the user like the app's. Inside the buyer area the app's client is used: one cache, kept live by the hub.
 */
function WithQueryClient({ children }: { children: ReactNode }) {
  const inherited = use(QueryClientContext);
  const [own] = useState(() => (inherited ? null : new QueryClient({ defaultOptions: { queries: { refetchOnWindowFocus: false, retry: 1 }, mutations: { retry: false } } })));

  return own ? <OwnClient client={own}>{children}</OwnClient> : children;
}

function OwnClient({ client, children }: { client: QueryClient; children: ReactNode }) {
  useDropCacheOnUserChange(client);
  return <QueryClientProvider client={client}>{children}</QueryClientProvider>;
}

/**
 * The notification menu behind the bell (loaded lazily by `NotificationBell`). It needs a session before it asks for the
 * inbox: on public pages nothing restores it, so a hint with a live refresh cookie restores it here, and a stale hint
 * ends in no bell instead of a redirect to login.
 */
function NotificationMenu() {
  const session = useSyncExternalStore(subscribe, getSession, () => null);
  const [restoreFailed, setRestoreFailed] = useState(false);

  useEffect(() => {
    if (!getSession()) {
      void refreshAccessToken().finally(() => setRestoreFailed(!getSession()));
    }
  }, []);

  if (!session) {
    return restoreFailed ? null : <BellButton count={0} disabled />;
  }

  return <Inbox />;
}

function Inbox() {
  const [open, setOpen] = useState(false);
  const query = useNotifications();
  const markAll = useMarkAllRead();
  const openItem = useOpenNotification(() => setOpen(false));
  const unread = unreadCountOf(query.data);
  const items = (query.data?.pages[0]?.items ?? []).slice(0, PAGE_SIZE);

  // The hub keeps the cache live inside the buyer area; elsewhere an old answer is refreshed when the popover opens.
  const onOpenChange = (next: boolean) => {
    setOpen(next);
    if (next && query.isSuccess && query.isStale && !query.isFetching) void query.refetch();
  };

  return (
    <Popover open={open} onOpenChange={onOpenChange}>
      <PopoverTrigger asChild>
        <BellButton count={unread} />
      </PopoverTrigger>
      <PopoverContent align="end" aria-label={t("notifications.title")} className="w-[min(24rem,calc(100vw-2rem))] gap-0 p-0">
        <div className="flex items-center justify-between gap-2 border-b px-4 py-2">
          <h2 className="text-sm font-semibold">{t("notifications.title")}</h2>
          {items.length > 0 ? (
            <Button variant="ghost" size="sm" disabled={unread === 0 || markAll.isPending} onClick={() => markAll.mutate()}>
              {t("notifications.markAllRead")}
            </Button>
          ) : null}
        </div>
        <div className="max-h-96 overflow-y-auto">
          {query.isPending ? (
            <MenuLoading />
          ) : query.isError && items.length === 0 ? (
            <div role="alert" className="flex flex-col items-center gap-3 px-4 py-8 text-center">
              <p className="text-sm text-muted-foreground">{t("notifications.loadFailed")}</p>
              <Button variant="outline" size="sm" onClick={() => void query.refetch()}>
                {t("common.retry")}
              </Button>
            </div>
          ) : items.length === 0 ? (
            <p className="px-4 py-8 text-center text-sm text-muted-foreground">{t("notifications.empty.title")}</p>
          ) : (
            <NotificationList items={items} onOpen={openItem} label={t("notifications.list")} />
          )}
        </div>
        <div className="border-t p-1">
          <Button asChild variant="ghost" size="sm" className="w-full">
            <Link href="/account/notifications" prefetch={false} onClick={() => setOpen(false)}>
              {t("notifications.viewAll")}
            </Link>
          </Button>
        </div>
      </PopoverContent>
    </Popover>
  );
}

function MenuLoading() {
  return (
    <div aria-busy className="flex flex-col gap-3 p-4">
      <p role="status" className="sr-only">
        {t("notifications.loading")}
      </p>
      {[0, 1, 2].map((i) => (
        <Skeleton key={i} className="h-12 w-full" />
      ))}
    </div>
  );
}

export default function NotificationMenuRoot() {
  return (
    <WithQueryClient>
      <NotificationMenu />
    </WithQueryClient>
  );
}
