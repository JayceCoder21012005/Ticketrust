import type { QueryClient } from "@tanstack/react-query";
import { useEffect } from "react";
import { getSession, subscribe } from "./session";

/**
 * The cache holds one person's orders and profile. When that person signs out (or the session ends) or another user takes
 * over, everything cached is dropped, so the next person on this browser never sees it. A token refresh or a profile edit
 * keeps the same user id and the cache; a session that is still being restored (no user yet) has nothing to drop.
 * `onUserChange` drops what lives outside the query cache too, and is told the new user id (null when the session
 * ended): the app areas wipe the offline ticket wallet only for another user, because sign-out and a refused refresh
 * wipe it where they happen, and a busy api that ends the session must not take the wallet away at the venue.
 */
export function useDropCacheOnUserChange(queryClient: QueryClient, onUserChange?: (next: string | null) => void) {
  useEffect(() => {
    let userId = getSession()?.user.id ?? null;
    return subscribe(() => {
      const next = getSession()?.user.id ?? null;
      if (userId !== null && next !== userId) {
        queryClient.clear();
        onUserChange?.(next);
      }

      userId = next;
    });
  }, [queryClient, onUserChange]);
}
