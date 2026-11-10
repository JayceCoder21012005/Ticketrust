import type { Metadata } from "next";
import { notFound } from "next/navigation";
import { Suspense } from "react";
import { BuyerStepView } from "@/features/checkout/buyer-step-view";
import { CheckoutSkeleton } from "@/features/checkout/checkout-view";
import { getEvent, isSlug } from "@/lib/catalog/api";
import { t } from "@/lib/i18n.server";

export const metadata: Metadata = { title: t("checkout.title"), robots: { index: false } };

/** Buyer details before the order exists (spec 08). Sign-in is enforced by proxy.ts; the api is the real guard. */
export default function BuyerStepPage({ params }: PageProps<"/events/[slug]/checkout">) {
  return (
    <Suspense fallback={<CheckoutSkeleton />}>
      <BuyerStepContent params={params} />
    </Suspense>
  );
}

async function BuyerStepContent({ params }: { params: PageProps<"/events/[slug]/checkout">["params"] }) {
  const { slug } = await params;
  const event = isSlug(slug) ? await getEvent(slug) : null;
  if (!event) {
    notFound();
  }

  return <BuyerStepView event={{ id: event.id, slug: event.slug, title: event.title }} />;
}
