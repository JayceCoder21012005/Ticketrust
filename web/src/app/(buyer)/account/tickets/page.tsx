import type { Metadata } from "next";
import { Suspense } from "react";
import { TicketsSkeleton, TicketsView } from "@/features/tickets/tickets-view";
import { t } from "@/lib/i18n.server";

export const metadata: Metadata = { title: t("tickets.title"), robots: { index: false } };

/**
 * The buyer's tickets (spec 10). Sign-in is enforced by proxy.ts; the api only lists the caller's own tickets. The view
 * reads the tab from the url, which is why it sits in a Suspense boundary (the page stays prerenderable).
 */
export default function TicketsPage() {
  return (
    <Suspense
      fallback={
        <div className="mx-auto w-full max-w-3xl px-4 py-10">
          <TicketsSkeleton />
        </div>
      }
    >
      <TicketsView />
    </Suspense>
  );
}
