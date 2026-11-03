"use client";

import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { useState, type ReactNode } from "react";
import { Toaster } from "@/components/ui/sonner";
import { TooltipProvider } from "@/components/ui/tooltip";

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

/** For the (buyer), organizer, admin and gate layouts. */
export function AppProviders({ children }: { children: ReactNode }) {
  const [queryClient] = useState(createQueryClient);

  return (
    <QueryClientProvider client={queryClient}>
      <TooltipProvider>
        {children}
        <Toaster richColors closeButton />
      </TooltipProvider>
    </QueryClientProvider>
  );
}
