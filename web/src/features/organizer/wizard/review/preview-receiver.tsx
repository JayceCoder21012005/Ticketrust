"use client";

import { useEffect, useState } from "react";
import { Skeleton } from "@/components/ui/skeleton";
import { EventDetailView } from "@/features/catalog/detail/event-detail-view";
import type { EventDetail } from "@/lib/catalog/types";
import { PREVIEW_MESSAGE, PREVIEW_READY } from "./preview-frame";

/**
 * Renders the spec 04 detail page from a draft posted by the parent wizard (same origin only). Importing the detail
 * view here puts its server copy in this organizer-only chunk; public pages are unaffected.
 */
export function PreviewReceiver() {
  const [event, setEvent] = useState<EventDetail | null>(null);

  useEffect(() => {
    const onMessage = (e: MessageEvent) => {
      if (e.origin === window.location.origin && e.source === window.parent && e.data?.type === PREVIEW_MESSAGE) {
        setEvent(e.data.event as EventDetail);
      }
    };
    window.addEventListener("message", onMessage);
    window.parent.postMessage({ type: PREVIEW_READY }, window.location.origin);
    return () => window.removeEventListener("message", onMessage);
  }, []);

  return event ? <EventDetailView event={event} /> : <Skeleton className="m-6 h-96" />;
}
