"use client";

import { lazy, Suspense, useSyncExternalStore } from "react";
import { hasSessionHint } from "@/lib/auth/session-hint";
import { RemindLink, RemindPlaceholder, type FollowSize } from "./follow-ui";

// The query client, the api calls and the toggle load only for a visitor with a session hint: an anonymous visitor to the
// event page (the 170 KB first-load budget) downloads just this gate and a link to login.
const FollowControl = lazy(() => import("./follow-control"));

const noSubscription = () => () => {};

type FollowButtonProps = {
  /** The event's GUID (`EventDetail.id`), not its slug. */
  eventId: string;
  /** The page this button is on: where login brings an anonymous visitor back to. */
  returnTo: string;
  size?: FollowSize;
  className?: string;
};

/**
 * "Nhắc tôi" (spec 09): follow an event to be reminded before its sale opens. A tiny island like `NotificationBell`: the
 * non-secret `tr_session` hint decides between the login link and the real toggle, which is loaded on demand.
 */
export function FollowButton({ eventId, returnTo, size = "lg", className }: FollowButtonProps) {
  const signedIn = useSyncExternalStore(noSubscription, () => hasSessionHint(document.cookie), () => false);

  if (!signedIn) {
    return <RemindLink path={returnTo} size={size} {...(className ? { className } : {})} />;
  }

  return (
    <Suspense fallback={<RemindPlaceholder size={size} {...(className ? { className } : {})} />}>
      <FollowControl eventId={eventId} path={returnTo} size={size} {...(className ? { className } : {})} />
    </Suspense>
  );
}
