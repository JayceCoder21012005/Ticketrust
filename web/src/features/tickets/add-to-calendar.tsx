"use client";

import { CalendarPlus } from "lucide-react";
import { Button } from "@/components/ui/button";
import { t } from "@/lib/i18n";
import { buildIcs, icsFileName } from "@/lib/tickets/ics";
import type { TicketDetail } from "@/lib/tickets/types";
import { walletHref } from "./sibling-nav";

/** What the calendar entry says about the ticket: tier and seat, and the link back to the wallet (never the code). */
function describe(ticket: TicketDetail, url: string): string {
  const seat = ticket.seat ? t("tickets.seat", "vi", ticket.seat) : t("tickets.wallet.seat.standing");
  return `${ticket.tierName} · ${seat}\n${t("tickets.wallet.calendar.openTicket", "vi", { url })}`;
}

/** "Thêm vào lịch": downloads an `.ics` made in the browser (works offline, no api call). */
export function AddToCalendar({ ticket }: { ticket: TicketDetail }) {
  const download = () => {
    const url = `${window.location.origin}${walletHref(ticket.id)}`;
    const ics = buildIcs({
      ticketId: ticket.id,
      title: ticket.event.title,
      startsAt: ticket.event.startsAt,
      endsAt: ticket.event.endsAt,
      location: `${ticket.event.venueName}, ${ticket.event.city}`,
      description: describe(ticket, url),
      url,
      now: new Date(),
    });
    const href = URL.createObjectURL(new Blob([ics], { type: "text/calendar;charset=utf-8" }));
    const anchor = document.createElement("a");
    anchor.href = href;
    anchor.download = icsFileName(ticket.event.slug);
    anchor.click();
    // Give the browser a moment to start the download before the blob goes.
    setTimeout(() => URL.revokeObjectURL(href), 1_000);
  };

  return (
    <Button variant="outline" size="lg" className="h-11 w-full" onClick={download}>
      <CalendarPlus aria-hidden />
      {t("tickets.wallet.calendar.add")}
    </Button>
  );
}
