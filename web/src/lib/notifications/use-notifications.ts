import { useInfiniteQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { useSessionPresent } from "@/lib/api/use-session-present";
import { t } from "@/lib/i18n";
import { listNotifications, markAllNotificationsRead, markNotificationRead } from "./api";
import { markAllReadInCache, markReadInCache } from "./cache";
import { notificationKeys, PAGE_SIZE } from "./keys";

/**
 * The inbox, newest first, a page at a time (keyset: the api's `nextCursor`). One cache entry feeds the bell's popover
 * (the first page, the latest 10), the list page and the badge (`pages[0].unreadCount`).
 */
export function useNotifications() {
  const signedIn = useSessionPresent();

  return useInfiniteQuery({
    queryKey: notificationKeys.list,
    queryFn: ({ pageParam }) => listNotifications(pageParam, PAGE_SIZE),
    initialPageParam: undefined as string | undefined,
    getNextPageParam: (last) => last.nextCursor ?? undefined,
    enabled: signedIn,
    staleTime: 30_000,
  });
}

/**
 * Optimistic: the badge and the row change at once; if the api refuses, the change is shown as failed. Either way the inbox is
 * fetched again once the request has settled, so a push or refetch that landed in between cannot leave a stale count.
 */
export function useMarkRead() {
  const client = useQueryClient();

  return useMutation({
    mutationFn: (id: string) => markNotificationRead(id),
    onMutate: async (id: string) => {
      await client.cancelQueries({ queryKey: notificationKeys.list });
      markReadInCache(client, id);
    },
    onError: () => {
      toast.error(t("notifications.markReadFailed"));
    },
    onSettled: () => client.invalidateQueries({ queryKey: notificationKeys.list }),
  });
}

export function useMarkAllRead() {
  const client = useQueryClient();

  return useMutation({
    mutationFn: () => markAllNotificationsRead(),
    onMutate: async () => {
      await client.cancelQueries({ queryKey: notificationKeys.list });
      markAllReadInCache(client);
    },
    onError: () => {
      toast.error(t("notifications.markAllFailed"));
    },
    onSettled: () => client.invalidateQueries({ queryKey: notificationKeys.list }),
  });
}
