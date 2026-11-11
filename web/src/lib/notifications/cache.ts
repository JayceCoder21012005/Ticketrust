import type { InfiniteData, QueryClient } from "@tanstack/react-query";
import { notificationKeys } from "./keys";
import type { NotificationItem, NotificationPage } from "./types";

export type InboxData = InfiniteData<NotificationPage, string | undefined>;

/** The inbox's unread count: every page carries the total at the time it was fetched, the first page is the newest. */
export const unreadCountOf = (data: { pages: NotificationPage[] } | undefined): number => data?.pages[0]?.unreadCount ?? 0;

const setCount = (data: InboxData, unreadCount: number): InboxData => ({ ...data, pages: data.pages.map((page) => ({ ...page, unreadCount })) });

/** A pushed notification goes on top of the newest page, once; it counts as unread. */
export function addNotification(client: QueryClient, item: NotificationItem): void {
  const old = client.getQueryData<InboxData>(notificationKeys.list);
  if (!old) {
    // Not loaded (or loading right now, maybe without this row): the next fetch will include it.
    void client.invalidateQueries({ queryKey: notificationKeys.list });
    return;
  }

  if (old.pages.some((page) => page.items.some((existing) => existing.id === item.id))) {
    return;
  }

  const [first, ...rest] = old.pages;
  if (!first) {
    return;
  }

  const next = { ...old, pages: [{ ...first, items: [item, ...first.items] }, ...rest] };
  client.setQueryData<InboxData>(notificationKeys.list, setCount(next, unreadCountOf(old) + 1));
}

/** Optimistic "read": only an item that is still unread lowers the count. */
export function markReadInCache(client: QueryClient, id: string): void {
  client.setQueryData<InboxData>(notificationKeys.list, (old) => {
    if (!old || !old.pages.some((page) => page.items.some((item) => item.id === id && !item.read))) {
      return old;
    }

    const pages = old.pages.map((page) => ({ ...page, items: page.items.map((item) => (item.id === id ? { ...item, read: true } : item)) }));
    return setCount({ ...old, pages }, Math.max(0, unreadCountOf(old) - 1));
  });
}

export function markAllReadInCache(client: QueryClient): void {
  client.setQueryData<InboxData>(notificationKeys.list, (old) => {
    if (!old) {
      return old;
    }

    const pages = old.pages.map((page) => ({ ...page, items: page.items.map((item) => ({ ...item, read: true })) }));
    return setCount({ ...old, pages }, 0);
  });
}
