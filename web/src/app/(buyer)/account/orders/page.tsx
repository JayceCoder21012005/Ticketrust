import type { Metadata } from "next";
import { OrdersView } from "@/features/orders/orders-view";
import { t } from "@/lib/i18n.server";

export const metadata: Metadata = { title: t("orders.title"), robots: { index: false } };

/** The buyer's order history (spec 08). Sign-in is enforced by proxy.ts; the api only lists the caller's own orders. */
export default function OrdersPage() {
  return <OrdersView />;
}
