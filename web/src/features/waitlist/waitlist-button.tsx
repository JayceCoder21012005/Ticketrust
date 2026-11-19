"use client";

import Link from "next/link";
import { lazy, Suspense, useSyncExternalStore } from "react";
import { Button } from "@/components/ui/button";
import { loginHref } from "@/features/follow/follow-ui";
import { hasSessionHint } from "@/lib/auth/session-hint";
import type { TicketTier } from "@/lib/catalog/types";
import { t } from "@/lib/i18n";
import { cn } from "@/lib/utils";

// The api calls, the dialog and the buyer copy load only for a visitor with a session hint (the public JS budget).
const WaitlistControl = lazy(() => import("./waitlist-control"));

const noSubscription = () => () => {};

export type WaitlistButtonProps = {
  eventId: string;
  slug: string;
  tiers: TicketTier[];
  /** Per-user cap of the sale; the dialog never offers more. */
  maxPerUser: number;
  /** The mobile bar: one button, no secondary links. */
  compact?: boolean;
  className?: string;
};

/**
 * "Hết vé — Vào danh sách chờ" (spec 13). A visitor without a session gets a link to login that comes back to the event; a
 * signed-in one gets the real control (join dialog, position, leave, offer link), loaded on demand.
 */
export function WaitlistButton(props: WaitlistButtonProps) {
  const signedIn = useSyncExternalStore(noSubscription, () => hasSessionHint(document.cookie), () => false);
  const size = props.compact ? "default" : "lg";

  if (!signedIn) {
    return (
      <Button asChild size={size} className={props.className}>
        <Link href={loginHref(`/events/${props.slug}`)} prefetch={false}>
          {t("catalog.cta.soldOut")}
        </Link>
      </Button>
    );
  }

  return (
    <Suspense fallback={<WaitlistPlaceholder compact={props.compact} className={props.className} />}>
      <WaitlistControl {...props} />
    </Suspense>
  );
}

/** Same size as the real button, so nothing jumps while the control and the entry load. */
export function WaitlistPlaceholder({ compact, className }: { compact?: boolean; className?: string }) {
  return (
    <Button type="button" size={compact ? "default" : "lg"} disabled aria-busy="true" className={cn("animate-pulse", className)}>
      {t("catalog.cta.soldOut")}
    </Button>
  );
}
