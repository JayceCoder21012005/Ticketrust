import { Ban, CircleCheck, CircleX, Clock, LoaderCircle, Undo2, type LucideIcon } from "lucide-react";
import { Badge } from "@/components/ui/badge";
import { statusLabelKey, statusToneOf, type StatusTone } from "@/lib/checkout/order-timeline";
import type { OrderStatus } from "@/lib/checkout/types";
import { t } from "@/lib/i18n";
import { cn } from "@/lib/utils";

const ICON: Record<OrderStatus, LucideIcon> = {
  Pending: Clock,
  AwaitingPayment: LoaderCircle,
  PaymentFailed: CircleX,
  Paid: CircleCheck,
  Cancelled: Ban,
  Expired: Clock,
  RefundPending: Undo2,
  Refunded: Undo2,
};

/** The colour sits on the icon only; the label stays in the foreground colour, so a status never reads by colour alone. */
export const TONE: Record<StatusTone, string> = {
  success: "text-success",
  warning: "text-warning",
  danger: "text-danger",
  info: "text-info",
  neutral: "text-muted-foreground",
};

export function StatusBadge({ status }: { status: OrderStatus }) {
  const Icon = ICON[status];
  return (
    <Badge variant="outline" className="h-6 gap-1.5 px-2.5 text-foreground">
      <Icon aria-hidden className={cn("size-3.5", TONE[statusToneOf(status)])} />
      {t(statusLabelKey(status))}
    </Badge>
  );
}
