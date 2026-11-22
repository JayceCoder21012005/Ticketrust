"use client";

import { useEffect, useMemo, useState } from "react";
import { CartesianGrid, Line, LineChart, XAxis, YAxis } from "recharts";
import { Button } from "@/components/ui/button";
import { ChartContainer, ChartLegend, ChartLegendContent, ChartTooltip, ChartTooltipContent, type ChartConfig } from "@/components/ui/chart";
import { getSalesSeries, type Bucket, type DashboardTier, type SalesSeries } from "@/lib/dashboard/api";

const label = (iso: string, bucket: Bucket) =>
  new Date(iso).toLocaleString("vi-VN", bucket === "1m" ? { hour: "2-digit", minute: "2-digit", timeZone: "Asia/Ho_Chi_Minh" } : { day: "2-digit", month: "2-digit", hour: "2-digit", timeZone: "Asia/Ho_Chi_Minh" });

/**
 * Tickets sold per minute (or hour), one line per tier (spec 15). Reloads the series when the live total changes, at most once
 * every 15 s, so a busy sale does not refetch every second.
 */
export function SalesChart({ eventId, tiers, sold }: { eventId: string; tiers: DashboardTier[]; sold: number }) {
  const [bucket, setBucket] = useState<Bucket>("1m");
  const [series, setSeries] = useState<SalesSeries | null>(null);
  const [tick, setTick] = useState(0);

  useEffect(() => {
    const timer = setTimeout(() => setTick((t) => t + 1), 15_000);
    return () => clearTimeout(timer);
  }, [sold, tick]);

  useEffect(() => {
    const controller = new AbortController();
    getSalesSeries(eventId, bucket, controller.signal).then(setSeries, () => undefined);
    return () => controller.abort();
  }, [eventId, bucket, tick]);

  const config = useMemo<ChartConfig>(() => Object.fromEntries(tiers.map((t) => [t.tierId, { label: t.name, color: t.color }])), [tiers]);
  const rows = useMemo(() => {
    const byTime = new Map<string, Record<string, number | string>>();
    for (const point of series?.points ?? []) {
      const row = byTime.get(point.at) ?? { at: point.at };
      row[point.tierId] = point.tickets;
      byTime.set(point.at, row);
    }
    return [...byTime.values()];
  }, [series]);

  return (
    <div className="flex flex-col gap-3">
      <div role="group" aria-label="Khoảng thời gian" className="flex gap-2">
        <Button type="button" size="sm" variant={bucket === "1m" ? "default" : "outline"} aria-pressed={bucket === "1m"} onClick={() => setBucket("1m")}>
          Theo phút
        </Button>
        <Button type="button" size="sm" variant={bucket === "1h" ? "default" : "outline"} aria-pressed={bucket === "1h"} onClick={() => setBucket("1h")}>
          Theo giờ
        </Button>
      </div>
      {rows.length === 0 ? (
        <p className="flex h-64 items-center justify-center text-sm text-muted-foreground">Chưa có vé nào được bán trong khoảng này.</p>
      ) : (
        <ChartContainer config={config} className="h-64 w-full">
          <LineChart data={rows} margin={{ left: 4, right: 12 }}>
            <CartesianGrid vertical={false} />
            <XAxis dataKey="at" tickFormatter={(value: string) => label(value, bucket)} tickLine={false} axisLine={false} minTickGap={24} />
            <YAxis allowDecimals={false} tickLine={false} axisLine={false} width={32} />
            <ChartTooltip content={<ChartTooltipContent labelFormatter={(value) => label(String(value), bucket)} />} />
            <ChartLegend content={<ChartLegendContent />} />
            {tiers.map((tier) => (
              <Line key={tier.tierId} dataKey={tier.tierId} name={tier.name} type="monotone" stroke={`var(--color-${tier.tierId})`} strokeWidth={2} dot={false} isAnimationActive={false} />
            ))}
          </LineChart>
        </ChartContainer>
      )}
    </div>
  );
}
