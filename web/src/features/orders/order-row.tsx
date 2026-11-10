import Link from "next/link";
import { Button } from "@/components/ui/button";
import { isPayable, orderCode } from "@/lib/checkout/order-state";
import type { OrderSummary } from "@/lib/checkout/types";
import { formatDateTime, formatVnd } from "@/lib/format";
import { t } from "@/lib/i18n";
import { StatusBadge } from "./status-badge";

export const orderLinkId = (id: string) => `order-link-${id}`;

/**
 * One order in the list. The event title is the link to the detail; an open order also offers the way back to paying.
 * The list does not carry the order deadline, so the pay link is shown for every open order and `/checkout/[id]`
 * decides (it hands an order that ran out over to its result page).
 */
export function OrderRow({ order }: { order: OrderSummary }) {
  const code = orderCode(order.id);
  return (
    <li className="flex flex-col gap-3 rounded-xl border bg-card p-4 sm:flex-row sm:items-center sm:justify-between">
      <div className="flex min-w-0 flex-col gap-1">
        <h2 className="text-base font-semibold">
          <Link id={orderLinkId(order.id)} href={`/account/orders/${order.id}`} className="rounded-sm underline-offset-4 hover:underline focus-visible:outline-2 focus-visible:outline-offset-2">
            {order.eventTitle || t("orders.eventFallback")}
            {" "}
            <span className="sr-only">{t("orders.code", "vi", { code })}</span>
          </Link>
        </h2>
        <p className="text-sm text-muted-foreground">
          {`${code} · ${t("orders.placedAt", "vi", { time: formatDateTime(order.createdAt) })} · ${t("orders.items", "vi", { count: order.itemCount })}`}
        </p>
      </div>
      <div className="flex flex-wrap items-center gap-3 sm:justify-end">
        <strong className="text-base tabular-nums">{formatVnd(order.total)}</strong>
        <StatusBadge status={order.status} />
        {isPayable(order.status) ? (
          <Button asChild size="sm">
            <Link href={`/checkout/${order.id}`}>
              {t("orders.continuePay")}
              {" "}
              <span className="sr-only">{t("orders.code", "vi", { code })}</span>
            </Link>
          </Button>
        ) : null}
      </div>
    </li>
  );
}
