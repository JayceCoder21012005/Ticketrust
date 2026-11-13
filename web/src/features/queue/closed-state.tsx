import { CalendarX, Clock, Lock, Ticket } from "lucide-react";
import Link from "next/link";
import { Button } from "@/components/ui/button";
import { t } from "@/lib/i18n.buyer";

type ClosedKind = "Closed" | "SoldOut" | "AllHeld" | "Expired";

const COPY = {
  Closed: { icon: CalendarX, key: "queue.closed" },
  SoldOut: { icon: Ticket, key: "queue.soldOut" },
  AllHeld: { icon: Lock, key: "queue.allHeld" },
  Expired: { icon: Clock, key: "queue.expired" },
} as const;

/**
 * End states with nothing to wait for. A sold-out sale will offer the waitlist (spec 13, later); until then it only
 * explains. An expired buyer can rejoin, at the tail.
 */
export function ClosedState({ kind, slug, onRejoin }: { kind: ClosedKind; slug: string; onRejoin: () => void }) {
  const { icon: Icon, key } = COPY[kind];

  return (
    <div className="flex flex-col items-center gap-4 text-center">
      <Icon aria-hidden className="size-9 text-muted-foreground" />
      <h2 className="text-xl font-semibold">{t(`${key}.title`)}</h2>
      <p className="max-w-xs text-sm text-muted-foreground">{t(`${key}.description`)}</p>
      {kind === "Expired" ? (
        <Button onClick={onRejoin}>{t("queue.expired.rejoin")}</Button>
      ) : (
        <Button asChild variant="outline">
          <Link href={`/events/${slug}`}>{t("queue.closed.back")}</Link>
        </Button>
      )}
    </div>
  );
}
