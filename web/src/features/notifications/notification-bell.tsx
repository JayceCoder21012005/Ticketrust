"use client";

import { lazy, Suspense, useSyncExternalStore } from "react";
import { hasSessionHint } from "@/lib/auth/session-hint";
import { BellButton } from "./bell-button";

// The popover, query client and inbox code load only for a visitor with a session hint: anonymous pages (the 170 KB
// first-load budget) ship just this gate and the placeholder button.
const NotificationMenu = lazy(() => import("./notification-menu"));

const noSubscription = () => () => {};

/**
 * Header bell (spec 09). A tiny island like `AccountLink`: the non-secret `tr_session` hint decides whether it renders at
 * all. It never opens the SignalR connection: that is the buyer layout's `UserHubProvider`, so the bell works on public
 * pages too (its count is then as of the last fetch, not live).
 */
export function NotificationBell() {
  const signedIn = useSyncExternalStore(noSubscription, () => hasSessionHint(document.cookie), () => false);

  if (!signedIn) {
    return null;
  }

  return (
    <Suspense fallback={<BellButton count={0} disabled />}>
      <NotificationMenu />
    </Suspense>
  );
}
