"use client";

import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { useState, type ReactNode } from "react";
import { Toaster } from "@/components/ui/sonner";
import { TooltipProvider } from "@/components/ui/tooltip";
import { useDropCacheOnUserChange } from "@/lib/api/use-drop-cache-on-user-change";
import { forgetOfflineTicketsLater } from "@/lib/tickets/forget-offline";

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
 * The offline ticket wallet (cached QR keys, ticket details, the service worker and its caches) belongs to one user:
 * another user taking over wipes it with the query cache. Sign-out wipes it in `AuthProvider.signOut` and a refused
 * refresh in `doRefresh`; a session that merely ended (a busy api, no network) must keep it. Loaded on demand.
 */
const forgetOfflineTickets = (next: string | null) => {
  if (next !== null) void forgetOfflineTicketsLater();
};

/** For the (buyer), organizer, admin and gate layouts. */
export function AppProviders({ children }: { children: ReactNode }) {
  const [queryClient] = useState(createQueryClient);
  useDropCacheOnUserChange(queryClient, forgetOfflineTickets);

  return (
    <QueryClientProvider client={queryClient}>
      <TooltipProvider>
        {children}
        <Toaster richColors closeButton />
      </TooltipProvider>
    </QueryClientProvider>
  );
}
