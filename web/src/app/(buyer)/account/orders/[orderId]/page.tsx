import type { Metadata } from "next";
import { Suspense } from "react";
import { OrderDetailSkeleton, OrderDetailView } from "@/features/orders/order-detail-view";
import { t } from "@/lib/i18n.server";

export const metadata: Metadata = { title: t("orders.title"), robots: { index: false } };

/** One order with its status timeline (spec 08). The api only shows an order to its owner. */
export default function OrderDetailPage({ params }: PageProps<"/account/orders/[orderId]">) {
  return (
    <Suspense fallback={<OrderDetailSkeleton />}>
      <OrderDetailContent params={params} />
    </Suspense>
  );
}

async function OrderDetailContent({ params }: { params: PageProps<"/account/orders/[orderId]">["params"] }) {
  const { orderId } = await params;
  return <OrderDetailView orderId={orderId} />;
}
