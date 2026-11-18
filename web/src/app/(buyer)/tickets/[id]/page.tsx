import type { Metadata } from "next";
import { Suspense } from "react";
import { WalletSkeleton, WalletView } from "@/features/tickets/wallet-view";
import { t } from "@/lib/i18n.server";

export const metadata: Metadata = { title: t("tickets.wallet.title"), robots: { index: false } };

/**
 * One ticket with its rotating QR (spec 10). Sign-in is enforced by proxy.ts; the api only shows a ticket to its owner.
 * The view is keyed by the id, so moving to another ticket of the order starts it afresh (its own key and code).
 */
export default function TicketWalletPage({ params }: PageProps<"/tickets/[id]">) {
  return (
    <Suspense fallback={<WalletSkeleton />}>
      <TicketWalletContent params={params} />
    </Suspense>
  );
}

async function TicketWalletContent({ params }: { params: PageProps<"/tickets/[id]">["params"] }) {
  const { id } = await params;
  return <WalletView key={id} ticketId={id} />;
}
