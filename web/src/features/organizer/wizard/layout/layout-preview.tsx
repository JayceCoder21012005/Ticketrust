import { memo, type MouseEvent } from "react";
import { CANVAS_HEIGHT, CANVAS_WIDTH, type GeneratedSeat } from "@/lib/organizer/seat-layout";
import type { SectionDefinition, TierItem } from "../../types";

export const STANDING_WIDTH = 240;
export const STANDING_HEIGHT = 120;
const DEFAULT_STANDING_ORIGIN = { x: 380, y: 400 };

type LayoutPreviewProps = {
  sections: SectionDefinition[];
  tiers: TierItem[];
  seats: GeneratedSeat[];
  activeSection: string | null;
  onSectionClick: (code: string) => void;
};

/**
 * Live SVG of the generated layout. One `<circle>` per seat and a single click handler on the `<svg>` (event
 * delegation), so a 600-seat section renders in well under 100 ms (spec 05 acceptance).
 */
export const LayoutPreview = memo(function LayoutPreview({ sections, tiers, seats, activeSection, onSectionClick }: LayoutPreviewProps) {
  const color = new Map(tiers.map((t) => [t.id, t.color]));
  const bySection = new Map<string, GeneratedSeat[]>();
  for (const seat of seats) {
    const list = bySection.get(seat.section);
    if (list) list.push(seat);
    else bySection.set(seat.section, [seat]);
  }

  const click = (e: MouseEvent<SVGSVGElement>) => {
    const code = (e.target as Element).closest("[data-section]")?.getAttribute("data-section");
    if (code) onSectionClick(code);
  };

  return (
    <svg
      viewBox={`0 0 ${CANVAS_WIDTH} ${CANVAS_HEIGHT}`}
      role="img"
      aria-label={`Xem trước sơ đồ: ${seats.length.toLocaleString("vi-VN")} ghế`}
      className="w-full rounded-xl border bg-muted/40"
      onClick={click}
    >
      <rect x={350} y={20} width={300} height={44} rx={8} className="fill-foreground/80" />
      <text x={500} y={48} textAnchor="middle" className="fill-background text-[18px] font-semibold">
        Sân khấu
      </text>
      {sections.map((section) => {
        const active = section.code === activeSection;
        if ((section.kind ?? "seated") === "standing") {
          const origin = section.origin ?? DEFAULT_STANDING_ORIGIN;
          return (
            <g key={section.code} data-section={section.code} className="cursor-pointer">
              <rect
                x={origin.x}
                y={origin.y}
                width={STANDING_WIDTH}
                height={STANDING_HEIGHT}
                rx={10}
                fill={color.get(section.tierId) ?? "#94a3b8"}
                opacity={0.85}
                strokeWidth={active ? 4 : 0}
                className="stroke-foreground"
              />
              <text x={origin.x + STANDING_WIDTH / 2} y={origin.y + STANDING_HEIGHT / 2 + 6} textAnchor="middle" className="fill-white text-[18px] font-semibold">
                {section.code}
              </text>
            </g>
          );
        }

        const sectionSeats = bySection.get(section.code) ?? [];
        return (
          <g key={section.code} data-section={section.code} className="cursor-pointer" opacity={activeSection && !active ? 0.55 : 1}>
            {sectionSeats.map((seat) => (
              <circle key={seat.index} cx={seat.x} cy={seat.y} r={8} fill={color.get(seat.tierId) ?? "#94a3b8"} />
            ))}
          </g>
        );
      })}
    </svg>
  );
});
