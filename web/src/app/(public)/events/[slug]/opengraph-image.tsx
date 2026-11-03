import { readFile } from "node:fs/promises";
import { join } from "node:path";
import { ImageResponse } from "next/og";
import { getEvent, isSlug } from "@/lib/catalog/api";
import { categoryName } from "@/lib/catalog/categories";
import { posterTheme } from "@/lib/catalog/poster";
import { formatDateTime } from "@/lib/format";

export const alt = "TicketRush";
export const size = { width: 1200, height: 630 };
export const contentType = "image/png";

/**
 * Social card per event (spec 04), drawn like the generated poster. Be Vietnam Pro is bundled because the
 * default OG font has no Vietnamese diacritics.
 */
export default async function OpenGraphImage({ params }: { params: Promise<{ slug: string }> }) {
  const { slug } = await params;
  const [event, font] = await Promise.all([
    isSlug(slug) ? getEvent(slug).catch(() => null) : null,
    readFile(join(process.cwd(), "assets/fonts/BeVietnamPro-Bold.ttf")),
  ]);

  const theme = posterTheme(slug, event?.category ?? "");

  return new ImageResponse(
    (
      <div
        style={{
          width: "100%",
          height: "100%",
          display: "flex",
          flexDirection: "column",
          justifyContent: "space-between",
          padding: 72,
          color: "white",
          fontFamily: "Be Vietnam Pro",
          backgroundImage: `linear-gradient(${theme.angle}deg, ${theme.from}, ${theme.to})`,
        }}
      >
        <div style={{ display: "flex", fontSize: 32, letterSpacing: 4, textTransform: "uppercase", opacity: 0.85 }}>
          {event ? categoryName(event.category) : "TicketRush"}
        </div>
        <div style={{ display: "flex", flexDirection: "column", gap: 24 }}>
          <div style={{ display: "flex", fontSize: 76, lineHeight: 1.1 }}>{event?.title ?? "TicketRush"}</div>
          {event ? (
            <div style={{ display: "flex", fontSize: 32, opacity: 0.9 }}>
              {formatDateTime(event.startsAt)} · {event.venue.name}, {event.venue.city}
            </div>
          ) : null}
        </div>
        <div style={{ display: "flex", fontSize: 30 }}>TicketRush</div>
      </div>
    ),
    { ...size, fonts: [{ name: "Be Vietnam Pro", data: font, weight: 700, style: "normal" }] },
  );
}
