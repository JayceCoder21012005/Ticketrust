import { X } from "lucide-react";
import Link from "next/link";
import { categoryName } from "@/lib/catalog/categories";
import { listingHref, type EventFilters } from "@/lib/catalog/filters";
import { formatVnd } from "@/lib/format";
import { t } from "@/lib/i18n.server";

const dayLabel = (day: string) => day.split("-").reverse().join("/");

/** Editable chips for the filters in use (DICE critique: filters stay visible and removable in place). No JS. */
export function ActiveFilters({ filters }: { filters: EventFilters }) {
  const chips: { key: string; label: string; without: EventFilters }[] = [];

  if (filters.category) chips.push({ key: "category", label: categoryName(filters.category), without: { ...filters, category: undefined } });
  if (filters.city) chips.push({ key: "city", label: filters.city, without: { ...filters, city: undefined } });
  if (filters.when) chips.push({ key: "when", label: t(`catalog.listing.${filters.when}`), without: { ...filters, when: undefined } });
  if (!filters.when && filters.from) {
    chips.push({ key: "from", label: `${t("catalog.listing.from")} ${dayLabel(filters.from)}`, without: { ...filters, from: undefined } });
  }
  if (!filters.when && filters.to) {
    chips.push({ key: "to", label: `${t("catalog.listing.to")} ${dayLabel(filters.to)}`, without: { ...filters, to: undefined } });
  }
  if (filters.priceMax !== undefined) {
    chips.push({
      key: "price",
      label: t("catalog.listing.priceUpTo", "vi", { price: formatVnd(filters.priceMax) }),
      without: { ...filters, priceMax: undefined },
    });
  }

  if (chips.length === 0) {
    return null;
  }

  return (
    <ul className="flex flex-wrap items-center gap-2">
      {chips.map(({ key, label, without }) => (
        <li key={key}>
          <Link
            href={listingHref(without)}
            scroll={false}
            aria-label={t("catalog.listing.removeFilter", "vi", { label })}
            className="flex h-8 items-center gap-1.5 rounded-full bg-secondary pl-3 pr-2 text-sm text-secondary-foreground hover:bg-secondary/80"
          >
            {label}
            <X aria-hidden className="size-3.5" />
          </Link>
        </li>
      ))}
      <li>
        <Link href={listingHref({ q: filters.q })} scroll={false} className="text-sm font-medium text-primary underline-offset-4 hover:underline">
          {t("catalog.listing.clearAll")}
        </Link>
      </li>
    </ul>
  );
}
