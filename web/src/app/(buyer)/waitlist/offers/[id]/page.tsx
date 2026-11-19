import type { Metadata } from "next";
import { Suspense } from "react";
import { Skeleton } from "@/components/ui/skeleton";
import { OfferView } from "@/features/waitlist/offer-view";
import { t } from "@/lib/i18n.buyer";

export const metadata: Metadata = { title: t("waitlist.offer.title"), robots: { index: false } };

/** "Có vé cho bạn!" (spec 13): the held tickets, a countdown and the way into checkout. Sign-in is enforced by proxy.ts. */
export default function WaitlistOfferRoute({ params }: PageProps<"/waitlist/offers/[id]">) {
  return (
    <div className="mx-auto w-full max-w-xl px-4 py-8">
      <Suspense fallback={<Skeleton className="h-80 w-full rounded-2xl" />}>
        <OfferContent params={params} />
      </Suspense>
    </div>
  );
}

async function OfferContent({ params }: { params: PageProps<"/waitlist/offers/[id]">["params"] }) {
  const { id } = await params;
  return <OfferView offerId={id} />;
}
