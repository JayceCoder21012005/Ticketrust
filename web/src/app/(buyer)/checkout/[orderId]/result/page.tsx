import type { Metadata } from "next";
import { Suspense } from "react";
import { ResultSkeleton, ResultView } from "@/features/checkout/result-view";
import { t } from "@/lib/i18n.server";

export const metadata: Metadata = { title: t("checkout.result.title"), robots: { index: false } };

/** Where the payment gateway returns the buyer (spec 08): follows the order until its outcome is known. */
export default function CheckoutResultPage({ params }: PageProps<"/checkout/[orderId]/result">) {
  return (
    <Suspense fallback={<ResultSkeleton />}>
      <ResultContent params={params} />
    </Suspense>
  );
}

async function ResultContent({ params }: { params: PageProps<"/checkout/[orderId]/result">["params"] }) {
  const { orderId } = await params;
  return <ResultView orderId={orderId} />;
}
