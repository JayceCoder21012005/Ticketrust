"use client";

import { useQueryClient } from "@tanstack/react-query";
import { useRouter } from "next/navigation";
import { createContext, use, useEffect, useEffectEvent, useRef, useState, type ReactNode } from "react";
import { toast } from "sonner";
import { useAuth } from "@/components/auth/auth-provider";
import { getAccessToken, refreshAccessToken } from "@/lib/api/session";
import { t } from "@/lib/i18n";
import { addNotification } from "@/lib/notifications/cache";
import { notificationKeys } from "@/lib/notifications/keys";
import { safeLink } from "@/lib/notifications/link";
import { connectUserFeed } from "@/lib/notifications/realtime";
import type { NotificationItem } from "@/lib/notifications/types";

type OrderStatusListener = (status: string) => void;

export type UserHub = {
  /** Calls `listener` with the new status whenever the hub reports a change of that order; returns the unsubscribe. */
  subscribeOrderStatus: (orderId: string, listener: OrderStatusListener) => () => void;
};

const noHub: UserHub = { subscribeOrderStatus: () => () => {} };
const UserHubContext = createContext<UserHub>(noHub);

/** The user hub of the buyer area. Outside it (or while signed out) subscribing does nothing: callers keep polling. */
export function useUserHub(): UserHub {
  return use(UserHubContext);
}

/**
 * The tab's one connection to `/hubs/user` (spec 09), mounted once by the buyer layout inside `AuthProvider` and
 * `AppProviders`: nothing else may open another. A pushed notification goes into the inbox cache (so the bell's badge
 * moves) and shows as a toast; every (re)connect refetches the inbox, because pushes are lost while the connection is
 * down and the server joins the user's group a moment after the handshake. `OrderStatusChanged` is fanned out to
 * `subscribeOrderStatus` listeners (the order result page, spec 08, which keeps polling as the fallback).
 */
export function UserHubProvider({ children }: { children: ReactNode }) {
  const { user } = useAuth();
  const userId = user?.id ?? null;
  const queryClient = useQueryClient();
  const router = useRouter();
  const listeners = useRef(new Map<string, Set<OrderStatusListener>>());

  const onNotification = useEffectEvent((item: NotificationItem) => {
    addNotification(queryClient, item);
    const href = safeLink(item.link);
    toast(item.title, { description: item.body, ...(href ? { action: { label: t("notifications.toastView"), onClick: () => router.push(href) } } : {}) });
  });

  const onOrderStatus = useEffectEvent((orderId: string, status: string) => {
    listeners.current.get(orderId)?.forEach((listener) => listener(status));
  });

  const onResync = useEffectEvent(() => {
    void queryClient.invalidateQueries({ queryKey: notificationKeys.list });
  });

  useEffect(() => {
    if (userId === null) {
      return;
    }

    const feed = connectUserFeed({
      accessToken: async () => getAccessToken() ?? (await refreshAccessToken()) ?? "",
      onNotification,
      onOrderStatus,
      onResync,
    });
    return () => {
      void feed.stop();
    };
  }, [userId]);

  const [hub] = useState<UserHub>(() => ({
    subscribeOrderStatus(orderId, listener) {
      const forOrder = listeners.current.get(orderId) ?? new Set();
      forOrder.add(listener);
      listeners.current.set(orderId, forOrder);
      return () => {
        forOrder.delete(listener);
        if (forOrder.size === 0) listeners.current.delete(orderId);
      };
    },
  }));

  return <UserHubContext value={hub}>{children}</UserHubContext>;
}
