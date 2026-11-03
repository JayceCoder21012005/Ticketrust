"use client";

import { useRouter } from "next/navigation";
import { useTransition, type ReactNode } from "react";
import { CATEGORIES } from "@/lib/catalog/categories";
import { DATE_PRESETS, listingHref, type EventFilters } from "@/lib/catalog/filters";
import { formatVnd } from "@/lib/format";
import { t } from "@/lib/i18n";
import { cn } from "@/lib/utils";

export const PRICE_STEPS = [300_000, 500_000, 1_000_000, 2_000_000] as const;

type FilterPanelProps = {
  filters: EventFilters;
  cities: string[];
  /** The panel is rendered twice (sidebar and phone dialog); radio groups must not share names across them. */
  namePrefix?: string;
  className?: string;
};

/**
 * Sidebar on desktop, inside the filter dialog on phones. Every choice updates the URL right away
 * (`router.replace`, no history entry per click), and the server renders the new results.
 */
export function FilterPanel({ filters, cities, namePrefix = "filter", className }: FilterPanelProps) {
  const router = useRouter();
  const [pending, startTransition] = useTransition();

  const apply = (next: EventFilters) => startTransition(() => router.replace(listingHref(next), { scroll: false }));

  return (
    <div aria-busy={pending} className={cn("flex flex-col gap-6", pending && "opacity-70 transition-opacity", className)}>
      <ChoiceGroup legend={t("catalog.listing.category")}>
        <Choice name={`${namePrefix}-category`} label={t("catalog.listing.any")} checked={!filters.category} onSelect={() => apply({ ...filters, category: undefined })} />
        {CATEGORIES.map(({ slug, name }) => (
          <Choice key={slug} name={`${namePrefix}-category`} label={name} checked={filters.category === slug} onSelect={() => apply({ ...filters, category: slug })} />
        ))}
      </ChoiceGroup>

      <ChoiceGroup legend={t("catalog.listing.city")}>
        <Choice name={`${namePrefix}-city`} label={t("catalog.listing.any")} checked={!filters.city} onSelect={() => apply({ ...filters, city: undefined })} />
        {cities.map((city) => (
          <Choice key={city} name={`${namePrefix}-city`} label={city} checked={filters.city === city} onSelect={() => apply({ ...filters, city })} />
        ))}
      </ChoiceGroup>

      <ChoiceGroup legend={t("catalog.listing.date")}>
        <Choice
          name={`${namePrefix}-when`}
          label={t("catalog.listing.any")}
          checked={!filters.when && !filters.from && !filters.to}
          onSelect={() => apply({ ...filters, when: undefined, from: undefined, to: undefined })}
        />
        {DATE_PRESETS.map((preset) => (
          <Choice
            key={preset}
            name={`${namePrefix}-when`}
            label={t(`catalog.listing.${preset}`)}
            checked={filters.when === preset}
            onSelect={() => apply({ ...filters, when: preset, from: undefined, to: undefined })}
          />
        ))}
        <div className="mt-1 grid w-full grid-cols-2 gap-2">
          <DateInput label={t("catalog.listing.from")} value={filters.when ? undefined : filters.from} onChange={(from) => apply({ ...filters, when: undefined, from })} />
          <DateInput label={t("catalog.listing.to")} value={filters.when ? undefined : filters.to} onChange={(to) => apply({ ...filters, when: undefined, to })} />
        </div>
      </ChoiceGroup>

      <ChoiceGroup legend={t("catalog.listing.price")}>
        <Choice name={`${namePrefix}-price`} label={t("catalog.listing.any")} checked={filters.priceMax === undefined} onSelect={() => apply({ ...filters, priceMax: undefined })} />
        {PRICE_STEPS.map((step) => (
          <Choice
            key={step}
            name={`${namePrefix}-price`}
            label={t("catalog.listing.priceUpTo", "vi", { price: formatVnd(step) })}
            checked={filters.priceMax === step}
            onSelect={() => apply({ ...filters, priceMax: step })}
          />
        ))}
      </ChoiceGroup>
    </div>
  );
}

function ChoiceGroup({ legend, children }: { legend: string; children: ReactNode }) {
  return (
    <fieldset className="flex flex-col gap-2">
      <legend className="mb-2 text-sm font-semibold">{legend}</legend>
      <div className="flex flex-wrap gap-2">{children}</div>
    </fieldset>
  );
}

/** A native radio styled as a chip: keyboard and screen-reader behaviour come for free. */
function Choice({ name, label, checked, onSelect }: { name: string; label: string; checked: boolean; onSelect: () => void }) {
  return (
    <label
      className={cn(
        "flex h-9 cursor-pointer items-center rounded-full border px-3 text-sm transition-colors hover:bg-muted has-[:focus-visible]:outline-2 has-[:focus-visible]:outline-offset-2 has-[:focus-visible]:outline-ring",
        checked && "border-primary bg-primary text-primary-foreground hover:bg-primary/90",
      )}
    >
      <input type="radio" name={name} checked={checked} onChange={onSelect} className="sr-only" />
      {label}
    </label>
  );
}

function DateInput({ label, value, onChange }: { label: string; value: string | undefined; onChange: (value: string | undefined) => void }) {
  return (
    <label className="flex flex-col gap-1 text-xs text-muted-foreground">
      {label}
      <input
        type="date"
        value={value ?? ""}
        onChange={(e) => onChange(e.target.value || undefined)}
        className="h-9 rounded-md border bg-background px-2 text-sm text-foreground"
      />
    </label>
  );
}
