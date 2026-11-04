import type { SectionDefinition, TicketKind, TierItem } from "../../types";

type Draft = Omit<SectionDefinition, "tierId">;

/** Starting points for the layout step (design §3.3): they only fill the form, every number stays editable. */
export const PRESETS: { id: "theater-3" | "stage-standing" | "stadium-4"; name: string; sections: (tiers: TierItem[]) => SectionDefinition[] }[] = [
  {
    id: "theater-3",
    name: "Nhà hát 3 khu",
    sections: withTiers([
      { code: "A", kind: "seated", rows: 10, seatsPerRow: 8, origin: { x: 90, y: 140 }, curve: 0.1 },
      { code: "B", kind: "seated", rows: 12, seatsPerRow: 14, origin: { x: 344, y: 120 }, curve: 0.15 },
      { code: "C", kind: "seated", rows: 10, seatsPerRow: 8, origin: { x: 742, y: 140 }, curve: 0.1 },
    ]),
  },
  {
    id: "stage-standing",
    name: "Sân khấu + khu đứng",
    sections: withTiers([
      { code: "GA", kind: "standing", origin: { x: 380, y: 110 }, curve: 0 },
      { code: "A", kind: "seated", rows: 8, seatsPerRow: 16, origin: { x: 320, y: 300 }, curve: 0.1 },
    ]),
  },
  {
    id: "stadium-4",
    name: "Sân vận động 4 phía",
    sections: withTiers([
      { code: "N", kind: "seated", rows: 6, seatsPerRow: 20, origin: { x: 272, y: 90 }, curve: 0 },
      { code: "S", kind: "seated", rows: 6, seatsPerRow: 20, origin: { x: 272, y: 520 }, curve: 0 },
      { code: "W", kind: "seated", rows: 12, seatsPerRow: 5, origin: { x: 90, y: 240 }, curve: 0 },
      { code: "E", kind: "seated", rows: 12, seatsPerRow: 5, origin: { x: 814, y: 240 }, curve: 0 },
    ]),
  },
];

/** Each section gets the first tier of its kind, or "" so the step can ask for one. */
function withTiers(drafts: Draft[]) {
  return (tiers: TierItem[]): SectionDefinition[] => {
    const first = (kind: TicketKind) => tiers.find((t) => t.kind === kind)?.id ?? "";
    return drafts.map((d) => ({ ...d, tierId: first(d.kind === "standing" ? "GeneralAdmission" : "Seated") }));
  };
}
