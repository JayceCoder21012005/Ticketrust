export const MIN_LEAD_MS = 10 * 60_000;

type Window = { id: string; opensAt: string; closesAt: string };

export type TimelineBlock = { id: string; leftPct: number; widthPct: number; overlaps: boolean; tooSoon: boolean };

/**
 * Lays sale phases on a bar running from now to the event start (design §3.3). Overlaps and phases opening within
 * 10 minutes are flagged as the organizer types, not at publish time. `now` is server time.
 */
export function layoutTimeline(phases: Window[], now: number, eventStart: string) {
  const end = Date.parse(eventStart);
  const span = Math.max(end - now, 1);
  const pct = (t: number) => Math.min(100, Math.max(0, ((t - now) / span) * 100));

  const parsed = phases
    .map((p) => ({ id: p.id, opens: Date.parse(p.opensAt), closes: Date.parse(p.closesAt) }))
    .filter((p) => Number.isFinite(p.opens) && Number.isFinite(p.closes) && p.closes > p.opens);

  const blocks: TimelineBlock[] = parsed.map((p) => {
    const left = pct(p.opens);
    return {
      id: p.id,
      leftPct: left,
      widthPct: pct(p.closes) - left,
      overlaps: parsed.some((other) => other !== p && p.opens < other.closes && other.opens < p.closes),
      tooSoon: p.opens - now < MIN_LEAD_MS,
    };
  });

  return { blocks, eventMarkPct: pct(end) };
}
