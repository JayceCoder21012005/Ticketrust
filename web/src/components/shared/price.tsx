import { formatVnd } from "@/lib/format";
import { t } from "@/lib/i18n";
import { cn } from "@/lib/utils";

type PriceProps = {
  /** Whole dong. All-in: the price shown is the price paid (DICE-style, no surprise fees). */
  amount: number;
  /** Prefix with "từ" for a starting price. */
  from?: boolean;
  className?: string;
};

export function Price({ amount, from = false, className }: PriceProps) {
  const formatted = formatVnd(amount);

  return (
    <span className={cn("font-semibold tabular-nums", className)}>
      {from ? t("price.from", "vi", { price: formatted }) : formatted}
    </span>
  );
}
