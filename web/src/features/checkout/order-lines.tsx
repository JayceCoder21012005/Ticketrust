import { formatVnd } from "@/lib/format";
import { t } from "@/lib/i18n.buyer";

type Seat = { index: number; section: string; row: string; number: string; price: number };
type GaLine = { tierId: string; name: string; quantity: number; price: number };

/** What both a hold and an order carry: the same lines, grouped for reading (spec 08). */
export type Lines = { seats: Seat[]; ga: GaLine[]; total: number };

/** Seats keep their order, grouped by section: `Khu A` then its rows. */
function bySection(seats: Seat[]): [string, Seat[]][] {
  const groups = new Map<string, Seat[]>();
  for (const seat of seats) {
    groups.set(seat.section, [...(groups.get(seat.section) ?? []), seat]);
  }

  return [...groups];
}

/** The ticket lines of an order, seats grouped by section, then general admission, then the total in large type. */
export function OrderLines({ lines, headingId }: { lines: Lines; headingId: string }) {
  return (
    <section aria-labelledby={headingId} className="flex flex-col gap-4">
      <h2 id={headingId} className="text-base font-semibold">
        {t("checkout.summary.title")}
      </h2>
      {bySection(lines.seats).map(([section, seats]) => (
        <div key={section} className="flex flex-col gap-1">
          <h3 className="text-sm font-medium text-muted-foreground">{t("checkout.summary.section", "vi", { name: section })}</h3>
          <ul className="flex flex-col gap-1 text-sm">
            {seats.map((seat) => (
              <li key={seat.index} className="flex justify-between gap-2">
                <span>{t("checkout.summary.seat", "vi", { row: seat.row, number: seat.number })}</span>
                <span className="tabular-nums">{formatVnd(seat.price)}</span>
              </li>
            ))}
          </ul>
        </div>
      ))}
      {lines.ga.length > 0 ? (
        <ul className="flex flex-col gap-1 text-sm">
          {lines.ga.map((line) => (
            <li key={line.tierId} className="flex justify-between gap-2">
              <span>{t("checkout.summary.ga", "vi", { name: line.name, quantity: line.quantity })}</span>
              <span className="tabular-nums">{formatVnd(line.price * line.quantity)}</span>
            </li>
          ))}
        </ul>
      ) : null}
      <p className="flex items-baseline justify-between gap-2 border-t pt-3">
        <span className="font-medium">{t("checkout.summary.total")}</span>
        <strong className="text-3xl font-bold tabular-nums">{formatVnd(lines.total)}</strong>
      </p>
    </section>
  );
}
