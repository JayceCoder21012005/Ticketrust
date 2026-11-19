"use client";

import dynamic from "next/dynamic";
import { useSyncExternalStore } from "react";
import { hasSessionHint } from "@/lib/auth/session-hint";

// "For you" needs the authenticated api client and its own cards: downloaded only for a signed-in visitor, after hydration,
// so it never counts against the public home's JS budget.
const ForYou = dynamic(() => import("./for-you").then((m) => m.ForYou), { ssr: false });

const noop = () => () => {};

export function ForYouSlot() {
  const signedIn = useSyncExternalStore(noop, () => hasSessionHint(document.cookie), () => false);
  return signedIn ? <ForYou /> : null;
}
