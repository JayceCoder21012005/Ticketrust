"use client";

import type { ReactNode } from "react";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { t } from "@/lib/i18n";
import type { TicketScope } from "@/lib/tickets/types";

export const SCOPES: readonly TicketScope[] = ["upcoming", "past"];

/** The scope a `?scope=` value names; anything else (missing, misspelled) is the default tab. */
export function parseScope(value: string | null): TicketScope {
  return SCOPES.find((scope) => scope === value) ?? "upcoming";
}

type TicketsTabsProps = {
  scope: TicketScope;
  onScopeChange: (scope: TicketScope) => void;
  /** The active tab's content: the panel shows it and is named after its tab. */
  children: ReactNode;
};

/**
 * "Sắp diễn ra" / "Đã qua" as a real tablist (Radix: arrow keys move and select, Home/End jump, one tab stop). The
 * selected tab is controlled from outside, so the url stays the single source of it.
 */
export function TicketsTabs({ scope, onScopeChange, children }: TicketsTabsProps) {
  return (
    <Tabs value={scope} onValueChange={(value) => onScopeChange(parseScope(value))} className="gap-4">
      <TabsList aria-label={t("tickets.tabs.label")} className="w-full sm:w-fit">
        {SCOPES.map((value) => (
          <TabsTrigger key={value} value={value} className="px-4">
            {t(`tickets.tabs.${value}`)}
          </TabsTrigger>
        ))}
      </TabsList>
      {/* Only the active panel is mounted: each scope is its own query, and the other tab has nothing to show yet. */}
      <TabsContent value={scope} className="text-base">
        {children}
      </TabsContent>
    </Tabs>
  );
}
