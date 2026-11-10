import type { Metadata } from "next";
import { Suspense } from "react";
import { CheckoutSkeleton, CheckoutView } from "@/features/checkout/checkout-view";
import { t } from "@/lib/i18n.server";

export const metadata: Metadata = { title: t("checkout.title"), robots: { index: false } };

/** Pay for one order (spec 08). Sign-in is enforced by proxy.ts; the api only shows an order to its owner. */
export default function CheckoutPage({ params }: PageProps<"/checkout/[orderId]">) {
  return (
    <Suspense fallback={<CheckoutSkeleton />}>
      <CheckoutContent params={params} />
    </Suspense>
  );
}

async function CheckoutContent({ params }: { params: PageProps<"/checkout/[orderId]">["params"] }) {
  const { orderId } = await params;
  return <CheckoutView orderId={orderId} />;
}
