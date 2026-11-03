import { Badge } from "@/components/ui/badge";
import type { EventSummary } from "@/lib/catalog/types";
import { formatDayMonth } from "@/lib/format";
import { t } from "@/lib/i18n.server";
import { cn } from "@/lib/utils";

type SaleBadgeProps = {
  event: Pick<EventSummary, "isOnSale" | "isSoldOut" | "nextSaleOpensAt">;
  className?: string;
};

/** Sale state on cards (Eventbrite-style urgency). Rendered from cached data, so it never shows a live countdown. */
export function SaleBadge({ event, className }: SaleBadgeProps) {
  if (event.isSoldOut) {
    return (
      <Badge variant="secondary" className={className}>
        {t("catalog.badge.soldOut")}
      </Badge>
    );
  }

  if (event.isOnSale) {
    return (
      <Badge className={cn("gap-1.5", className)}>
        <span aria-hidden className="size-1.5 rounded-full bg-current motion-safe:animate-pulse" />
        {t("catalog.badge.onSale")}
      </Badge>
    );
  }

  if (event.nextSaleOpensAt) {
    return (
      <Badge variant="outline" className={cn("bg-background text-foreground", className)}>
        {t("catalog.badge.opens", "vi", { time: formatDayMonth(event.nextSaleOpensAt) })}
      </Badge>
    );
  }

  return null;
}
