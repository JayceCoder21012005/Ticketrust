import { readFileSync } from "node:fs";
import { join } from "node:path";
import type { Metadata } from "next";
import { notFound } from "next/navigation";
import type { ReactNode } from "react";
import { EmptyState } from "@/components/shared/empty-state";
import { EventCard } from "@/components/shared/event-card";
import { PageHeader } from "@/components/shared/page-header";
import { Price } from "@/components/shared/price";
import { StickyActionBar } from "@/components/shared/sticky-action-bar";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Skeleton } from "@/components/ui/skeleton";
import { parseThemeTokens, type TokenSet } from "@/lib/design-tokens";
import { isDesignPageEnabled } from "@/lib/feature-flags";
import { CountdownDemo, ErrorStateDemo } from "./interactive-demos";
import { TokenSwatches } from "./token-swatches";

export const metadata: Metadata = { title: "Design system", robots: { index: false } };

const coreTokens = ["background", "foreground", "primary", "secondary", "muted", "accent", "border", "ring"] as const;
const statusTokens = ["success", "warning", "danger", "info"] as const;
const seatTokens = [
  "seat-available-1",
  "seat-available-2",
  "seat-available-3",
  "seat-available-4",
  "seat-available-5",
  "seat-available-6",
  "seat-held",
  "seat-sold",
  "seat-selected",
] as const;

const vietnameseSample = "Sơn Tùng M-TP — Chân Trời Rực Rỡ: ắ ằ ẳ ẵ ặ ấ ầ ẩ ẫ ậ ơ ở ỡ ợ ư ừ ử ữ ự đ Đ";
const weights = [
  { weight: 400, className: "font-normal" },
  { weight: 500, className: "font-medium" },
  { weight: 600, className: "font-semibold" },
  { weight: 700, className: "font-bold" },
];

const sampleEvent = {
  slug: "son-tung-mtp-live",
  title: "Sơn Tùng M-TP Live",
  startsAt: "2026-12-31T13:00:00Z",
  venueName: "Sân vận động Mỹ Đình",
  city: "Hà Nội",
  coverImageUrl: null,
  priceFrom: 800_000,
};

export default function DesignPage() {
  if (!isDesignPageEnabled()) {
    notFound();
  }

  return (
    <div className="mx-auto flex w-full max-w-content flex-col gap-12 px-4 py-10 pb-28">
      <PageHeader title="Design system" description="Tokens, typography and shared components in light and dark." />

      <Section title="Typography · Be Vietnam Pro">
        <div className="flex flex-col gap-2">
          {weights.map(({ weight, className }) => (
            <p key={weight} className={`text-lg ${className}`}>
              <span className="mr-3 inline-block w-10 text-sm text-muted-foreground">{weight}</span>
              {vietnameseSample}
            </p>
          ))}
        </div>
      </Section>

      <ThemePanels>
        {(values) => (
          <>
            <h3 className="font-semibold">Core</h3>
            <TokenSwatches names={coreTokens} values={values} />
            <h3 className="font-semibold">Status</h3>
            <TokenSwatches names={statusTokens} values={values} />
            <h3 className="font-semibold">Seats (≥ 3:1 vs background)</h3>
            <TokenSwatches names={seatTokens} values={values} minContrast={3} />
            <div className="flex flex-wrap gap-2">
              <Button>Mua vé</Button>
              <Button variant="secondary">Xem thêm</Button>
              <Button variant="outline">Huỷ</Button>
              <Button disabled>Đang xử lý…</Button>
            </div>
            <div className="flex flex-wrap gap-2">
              <Badge>Đang mở bán</Badge>
              <Badge variant="secondary">Sắp mở bán</Badge>
              <Badge variant="outline">Đã kết thúc</Badge>
            </div>
          </>
        )}
      </ThemePanels>

      <Section title="Shared components">
        <div className="grid gap-6 md:grid-cols-2">
          <div className="flex flex-col gap-3">
            <Price amount={1_500_000} className="text-2xl" />
            <Price amount={800_000} from />
            <CountdownDemo />
          </div>
          <EventCard event={sampleEvent} />
          <EmptyState title="Chưa có vé nào" description="Vé bạn mua sẽ xuất hiện ở đây." />
          <ErrorStateDemo />
          <div className="flex flex-col gap-2" aria-hidden>
            <Skeleton className="aspect-video w-full" />
            <Skeleton className="h-5 w-3/4" />
            <Skeleton className="h-4 w-1/2" />
          </div>
        </div>
      </Section>

      <StickyActionBar label="Mua vé">
        <Price amount={800_000} from className="flex-1" />
        <Button>Mua vé</Button>
      </StickyActionBar>
    </div>
  );
}

function Section({ title, children }: { title: string; children: ReactNode }) {
  return (
    <section className="flex flex-col gap-4">
      <h2 className="text-xl font-semibold">{title}</h2>
      {children}
    </section>
  );
}

/** Declared token values, read at render time (traced into the standalone build). */
function readThemeTokens() {
  return parseThemeTokens(readFileSync(join(process.cwd(), "src", "app", "globals.css"), "utf8"));
}

/** Renders the same content under the light and the dark token set, side by side. */
function ThemePanels({ children }: { children: (values: TokenSet) => ReactNode }) {
  const { light, dark } = readThemeTokens();
  return (
    <section className="flex flex-col gap-4">
      <h2 className="text-xl font-semibold">Tokens</h2>
      <div className="grid gap-6 lg:grid-cols-2">
        <div className="light flex flex-col gap-4 rounded-xl border bg-background p-6 text-foreground">
          <p className="text-sm font-medium text-muted-foreground">Light</p>
          {children(light)}
        </div>
        <div className="dark flex flex-col gap-4 rounded-xl border bg-background p-6 text-foreground">
          <p className="text-sm font-medium text-muted-foreground">Dark</p>
          {children(dark)}
        </div>
      </div>
    </section>
  );
}
