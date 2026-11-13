"use client";

import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { useState, type ReactNode } from "react";
import { Toaster } from "@/components/ui/sonner";
import { TooltipProvider } from "@/components/ui/tooltip";
import { useDropCacheOnUserChange } from "@/lib/api/use-drop-cache-on-user-change";

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
 * sign-out and a user change wipe it with the query cache. Loaded on demand, so only that moment pays for it.
 */
const forgetOfflineTickets = () => {
  void import("@/lib/tickets/register-sw").then(
    (module) => module.forgetOfflineTickets(),
    () => undefined,
  );
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
