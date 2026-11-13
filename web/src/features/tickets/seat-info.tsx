import { useId } from "react";
import { t } from "@/lib/i18n.buyer";
import type { TicketSeat } from "@/lib/tickets/types";

type SeatInfoProps = {
  seat: TicketSeat | null;
  tierName: string;
  holderName: string;
};

/** Khu / Hàng / Ghế in large type, readable at arm's length by staff; "Vé đứng" for general admission. */
export function SeatInfo({ seat, tierName, holderName }: SeatInfoProps) {
  const headingId = useId();
  const cells = seat
    ? [
        [t("tickets.wallet.seat.section"), seat.section],
        [t("tickets.wallet.seat.row"), seat.row],
        [t("tickets.wallet.seat.number"), seat.number],
      ]
    : null;

  return (
    <section aria-labelledby={headingId} className="flex flex-col gap-3">
      <h2 id={headingId} className="sr-only">
        {t("tickets.wallet.seat.heading")}
      </h2>
      {cells ? (
        <dl className="grid grid-cols-3 gap-2 text-center">
          {cells.map(([label, value]) => (
            <div key={label} className="flex min-w-0 flex-col gap-1 rounded-xl bg-muted/60 px-2 py-3">
              <dt className="text-xs font-medium uppercase tracking-wide text-muted-foreground">{label}</dt>
              <dd className="break-words text-3xl font-bold leading-none tabular-nums sm:text-4xl">{value}</dd>
            </div>
          ))}
        </dl>
      ) : (
        <p className="rounded-xl bg-muted/60 px-4 py-3 text-center text-3xl font-bold">{t("tickets.wallet.seat.standing")}</p>
      )}
      <dl className="grid grid-cols-1 gap-x-4 gap-y-2 text-sm min-[360px]:grid-cols-2">
        <div className="min-w-0">
          <dt className="text-muted-foreground">{t("tickets.wallet.seat.tier")}</dt>
          <dd className="break-words text-base font-semibold">{tierName}</dd>
        </div>
        <div className="min-w-0">
          <dt className="text-muted-foreground">{t("tickets.wallet.seat.holder")}</dt>
          <dd className="break-words text-base font-semibold">{holderName}</dd>
        </div>
      </dl>
    </section>
  );
}
