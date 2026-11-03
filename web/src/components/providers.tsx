"use client";

import { ThemeProvider } from "next-themes";
import type { ReactNode } from "react";

/**
 * Root providers stay tiny: every byte here ships on public pages (170 KB first-load budget).
 * Data fetching, tooltips and toasts live in `AppProviders`, mounted by the app areas that need them.
 */
export function Providers({ children }: { children: ReactNode }) {
  return (
    <ThemeProvider attribute="class" defaultTheme="system" enableSystem disableTransitionOnChange>
      {children}
    </ThemeProvider>
  );
}
