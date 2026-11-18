import { useQuery } from "@tanstack/react-query";
import { useSessionPresent } from "@/lib/api/use-session-present";
import { getMyTickets, getTicket } from "./api";
import { ticketKeys } from "./keys";
import type { TicketScope } from "./types";

/**
 * The wallet's queries. They run in the buyer area, whose client drops the whole cache when the user changes
 * (`useDropCacheOnUserChange`), so one person's tickets never show for the next. They wait for a session.
 */
export function useMyTickets(scope: TicketScope) {
  const signedIn = useSessionPresent();

  return useQuery({
    queryKey: ticketKeys.list(scope),
    queryFn: ({ signal }) => getMyTickets(scope, signal),
    enabled: signedIn,
    // Always refetch on mount: a buyer who opened an empty "Sắp diễn ra" tab, bought, and came back must see the new ticket.
    staleTime: 0,
  });
}

export function useTicket(id: string) {
  const signedIn = useSessionPresent();

  return useQuery({
    queryKey: ticketKeys.detail(id),
    queryFn: ({ signal }) => getTicket(id, signal),
    enabled: signedIn,
  });
}
