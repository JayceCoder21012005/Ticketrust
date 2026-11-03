import { getEvent, isSlug } from "@/lib/catalog/api";
import { buildIcs } from "@/lib/catalog/ics";

/** "Thêm vào lịch": a plain download link, so the event page needs no JavaScript for it. */
export async function GET(request: Request, { params }: RouteContext<"/events/[slug]/calendar.ics">): Promise<Response> {
  const { slug } = await params;
  const event = isSlug(slug) ? await getEvent(slug) : null;
  if (!event) {
    return new Response("Not found", { status: 404 });
  }

  const ics = buildIcs({
    id: event.id,
    title: event.title,
    startsAt: event.startsAt,
    endsAt: event.endsAt,
    venueName: event.venue.name,
    venueAddress: event.venue.address,
    city: event.venue.city,
    url: new URL(`/events/${event.slug}`, request.url).toString(),
  });

  return new Response(ics, {
    headers: {
      "content-type": "text/calendar; charset=utf-8",
      "content-disposition": `attachment; filename="${event.slug}.ics"`,
      "cache-control": "public, max-age=600",
    },
  });
}
