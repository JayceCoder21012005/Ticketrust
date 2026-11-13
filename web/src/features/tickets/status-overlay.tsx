import { CalendarX, CircleCheck, CircleX, Tag, TriangleAlert, type LucideIcon } from "lucide-react";
import { t } from "@/lib/i18n.buyer";
import { cn } from "@/lib/utils";

/** Why the wallet shows no QR. `notDisplayable`: the api refused a key (409) and the ticket is being reloaded. */
export type OverlayKind = "checkedIn" | "listed" | "voided" | "ended" | "notDisplayable";

const icons: Record<OverlayKind, { icon: LucideIcon; tone: string }> = {
  checkedIn: { icon: CircleCheck, tone: "text-success" },
  listed: { icon: Tag, tone: "text-primary" },
  voided: { icon: CircleX, tone: "text-danger" },
  ended: { icon: CalendarX, tone: "text-muted-foreground" },
  notDisplayable: { icon: TriangleAlert, tone: "text-warning" },
};

/**
 * Takes the QR's place (same square) when the ticket cannot be shown at the gate. Check-in shows no time: spec 11
 * adds it, nothing is invented here.
 */
export function StatusOverlay({ kind }: { kind: OverlayKind }) {
  const { icon: Icon, tone } = icons[kind];

  return (
    <div className="flex aspect-square w-full max-w-80 flex-col items-center justify-center gap-3 rounded-2xl border-2 border-dashed bg-muted/40 p-6 text-center">
      <Icon aria-hidden className={cn("size-14", tone)} />
      <h2 className="text-2xl font-bold">{t(`tickets.wallet.overlay.${kind}.title`)}</h2>
      <p className="max-w-60 text-sm text-muted-foreground">{t(`tickets.wallet.overlay.${kind}.description`)}</p>
    </div>
  );
}
