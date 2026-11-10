"use client";

import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { useEffect, useState, type ReactNode } from "react";
import { Toaster } from "@/components/ui/sonner";
import { TooltipProvider } from "@/components/ui/tooltip";
import { getSession, subscribe } from "@/lib/api/session";

/**
 * TanStack Query is only for user-specific / live data (public pages use "use cache").
 * No refetch-on-focus by default: during checkout a surprise refetch must never change what the user sees.
 */
function createQueryClient() {
  return new QueryClient({
    defaultOptions: {
      queries: { refetchOnWindowFocus: false, retry: 1, staleTime: 30_000 },
      mutations: { retry: false },
    },
  });
}

/**
 * The cache holds one person's orders and profile. When that person signs out (or the session ends) or another user takes
 * over, everything cached is dropped, so the next person on this browser never sees it. A token refresh or a profile edit
 * keeps the same user id and the cache; a session that is still being restored (no user yet) has nothing to drop.
 */
function useDropCacheOnUserChange(queryClient: QueryClient) {
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

/** For the (buyer), organizer, admin and gate layouts. */
export function AppProviders({ children }: { children: ReactNode }) {
  const [queryClient] = useState(createQueryClient);
  useDropCacheOnUserChange(queryClient);

  return (
    <QueryClientProvider client={queryClient}>
      <TooltipProvider>
        {children}
        <Toaster richColors closeButton />
      </TooltipProvider>
    </QueryClientProvider>
  );
}
