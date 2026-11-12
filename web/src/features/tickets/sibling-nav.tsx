import { ChevronLeft, ChevronRight, type LucideIcon } from "lucide-react";
import Link from "next/link";
import { Button } from "@/components/ui/button";
import { t } from "@/lib/i18n";

export const walletHref = (id: string) => `/tickets/${id}`;

/** The tickets before and after `currentId` in its order; null at either end. */
export function siblingsOf(ids: readonly string[], currentId: string): { previous: string | null; next: string | null; index: number } {
  const index = ids.indexOf(currentId);
  if (index < 0) return { previous: null, next: null, index };
  return { previous: ids[index - 1] ?? null, next: ids[index + 1] ?? null, index };
}

function Step({ id, label, icon: Icon }: { id: string | null; label: string; icon: LucideIcon }) {
  if (!id) {
    return (
      <Button variant="outline" size="icon" className="size-11" disabled aria-label={label}>
        <Icon aria-hidden />
      </Button>
    );
  }

  return (
    <Button asChild variant="outline" size="icon" className="size-11">
      {/* A replace: the back button leaves the wallet instead of walking back through the order's tickets. */}
      <Link href={walletHref(id)} replace scroll={false} prefetch={false} aria-label={label}>
        <Icon aria-hidden />
      </Link>
    </Button>
  );
}

/** "‹ 2/4 ›" between the tickets of one order. Nothing for an order of one ticket. */
export function SiblingNav({ ids, currentId }: { ids: readonly string[]; currentId: string }) {
  const { previous, next, index } = siblingsOf(ids, currentId);
  if (ids.length < 2 || index < 0) return null;

  return (
    <nav aria-label={t("tickets.wallet.siblings.label")} className="flex flex-col items-center gap-1">
      <div className="flex items-center gap-4">
        <Step id={previous} label={t("tickets.wallet.siblings.previous")} icon={ChevronLeft} />
        <p className="min-w-12 text-center text-base font-semibold tabular-nums">
          <span aria-hidden>{`${index + 1}/${ids.length}`}</span>
          <span className="sr-only">{t("tickets.wallet.siblings.position", "vi", { index: index + 1, count: ids.length })}</span>
        </p>
        <Step id={next} label={t("tickets.wallet.siblings.next")} icon={ChevronRight} />
      </div>
      <p className="text-xs text-muted-foreground">{t("tickets.wallet.siblings.hint")}</p>
    </nav>
  );
}
