import type { QueryClient } from "@tanstack/react-query";
import { useEffect } from "react";
import { getSession, subscribe } from "./session";

/**
 * The cache holds one person's orders and profile. When that person signs out (or the session ends) or another user takes
 * over, everything cached is dropped, so the next person on this browser never sees it. A token refresh or a profile edit
 * keeps the same user id and the cache; a session that is still being restored (no user yet) has nothing to drop.
 */
export function useDropCacheOnUserChange(queryClient: QueryClient) {
  useEffect(() => {
    let userId = getSession()?.user.id ?? null;
    return subscribe(() => {
      const next = getSession()?.user.id ?? null;
      if (userId !== null && next !== userId) {
        queryClient.clear();
      }

      userId = next;
    });
  }, [queryClient]);
}
