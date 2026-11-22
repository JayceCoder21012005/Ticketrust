"use client";

import { Download, Radio, WifiOff } from "lucide-react";
import dynamic from "next/dynamic";
import Link from "next/link";
import { useEffect, useRef, useState } from "react";
import { ErrorState } from "@/components/shared/error-state";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Progress } from "@/components/ui/progress";
import { Skeleton } from "@/components/ui/skeleton";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import { connectDashboardFeed, downloadAttendees, getDashboard, type DashboardFunnel, type DashboardSnapshot } from "@/lib/dashboard/api";
import { ApiError, toFriendlyMessage } from "@/lib/errors";
import { formatVnd } from "@/lib/format";
import { cn } from "@/lib/utils";

// The heavy parts load after the shell: Recharts for the chart, the seat map for the heatmap (spec 15).
const SalesChart = dynamic(() => import("./sales-chart").then((m) => m.SalesChart), { ssr: false, loading: () => <Skeleton className="h-64 w-full" /> });
const SeatHeatmap = dynamic(() => import("./seat-heatmap").then((m) => m.SeatHeatmap), { ssr: false, loading: () => <Skeleton className="aspect-[10/7] w-full" /> });

const timeOf = (iso: string) => new Date(iso).toLocaleTimeString("vi-VN", { hour: "2-digit", minute: "2-digit", second: "2-digit", timeZone: "Asia/Ho_Chi_Minh" });
const count = (n: number) => n.toLocaleString("vi-VN");

type State = { kind: "loading" } | { kind: "ready"; snapshot: DashboardSnapshot } | { kind: "notFound" } | { kind: "failed"; message: string };

/**
 * `/organizer/events/[id]/dashboard` (spec 15). The shell renders from `GET …/dashboard`; the live feed then replaces the snapshot
 * once a second while the event is on sale. Without the feed the last numbers stay, with when they were taken.
 */
export function DashboardView({ eventId }: { eventId: string }) {
  const [state, setState] = useState<State>({ kind: "loading" });
  const [live, setLive] = useState(false);
  const [exporting, setExporting] = useState(false);
  const [exportError, setExportError] = useState<string | null>(null);

  useEffect(() => {
    const controller = new AbortController();
    getDashboard(eventId, controller.signal).then(
      (snapshot) => setState({ kind: "ready", snapshot }),
      (error: unknown) => {
        if (controller.signal.aborted) return;
        setState(error instanceof ApiError && error.status === 404 ? { kind: "notFound" } : { kind: "failed", message: toFriendlyMessage(error) });
      },
    );
    const feed = connectDashboardFeed(eventId, {
      onSnapshot: (snapshot) => setState({ kind: "ready", snapshot }),
      onStatus: setLive,
    });
    return () => {
      controller.abort();
      void feed.stop();
    };
  }, [eventId]);

  if (state.kind === "loading") return <DashboardSkeleton />;
  if (state.kind === "notFound") return <ErrorState title="Không tìm thấy sự kiện" description="Sự kiện không tồn tại hoặc không thuộc về bạn." />;
  if (state.kind === "failed") return <ErrorState title="Không tải được bảng điều khiển" description={state.message} onRetry={() => window.location.reload()} />;

  const { snapshot } = state;
  const exportCsv = async () => {
    setExporting(true);
    setExportError(null);
    try {
      await downloadAttendees(eventId);
    } catch (error) {
      setExportError(toFriendlyMessage(error));
    } finally {
      setExporting(false);
    }
  };

  return (
    <div className="flex flex-col gap-6">
      <header className="flex flex-wrap items-center justify-between gap-3">
        <div className="flex flex-col gap-1">
          <h1 className="text-2xl font-bold tracking-tight">Bảng điều khiển bán vé</h1>
          <Link href={`/organizer/events/${eventId}/info`} className="text-sm text-muted-foreground hover:underline">
            Sửa sự kiện
          </Link>
        </div>
        <LiveBadge live={live} at={snapshot.at} />
      </header>

      <section aria-label="Chỉ số chính" className="grid grid-cols-2 gap-3 md:grid-cols-5">
        <Kpi label="Đã bán" value={snapshot.sold} format={count} />
        <Kpi label="Doanh thu" value={snapshot.revenue} format={formatVnd} />
        <Kpi label="Còn lại" value={snapshot.remaining} format={count} />
        <Kpi label="Đang trong hàng chờ" value={snapshot.queue.waiting} format={count} />
        <Kpi label="Đang mua" value={snapshot.queue.activeShoppers} format={count} />
      </section>

      <div className="grid gap-6 lg:grid-cols-3">
        <Card className="lg:col-span-2">
          <CardHeader>
            <CardTitle>Vé bán mỗi phút</CardTitle>
          </CardHeader>
          <CardContent>
            <SalesChart eventId={eventId} tiers={snapshot.tiers} sold={snapshot.sold} />
          </CardContent>
        </Card>
        <Card>
          <CardHeader>
            <CardTitle>Phễu mua vé</CardTitle>
          </CardHeader>
          <CardContent>
            <Funnel funnel={snapshot.funnel} />
          </CardContent>
        </Card>
      </div>

      <Card>
        <CardHeader>
          <CardTitle>Theo hạng vé</CardTitle>
        </CardHeader>
        <CardContent className="overflow-x-auto">
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Hạng vé</TableHead>
                <TableHead className="text-right">Sức chứa</TableHead>
                <TableHead className="text-right">Đã bán</TableHead>
                <TableHead className="text-right">Đang giữ</TableHead>
                <TableHead className="text-right">Còn lại</TableHead>
                <TableHead className="text-right">Doanh thu</TableHead>
                <TableHead className="w-40">Tiến độ</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {snapshot.tiers.map((tier) => {
                const percent = tier.capacity > 0 ? Math.min(100, Math.round((tier.sold / tier.capacity) * 100)) : 0;
                return (
                  <TableRow key={tier.tierId}>
                    <TableCell className="font-medium">
                      <span className="flex items-center gap-2">
                        <span aria-hidden className="size-2.5 rounded-full" style={{ backgroundColor: tier.color }} />
                        {tier.name}
                      </span>
                    </TableCell>
                    <TableCell className="text-right tabular-nums">{count(tier.capacity)}</TableCell>
                    <TableCell className="text-right tabular-nums">{count(tier.sold)}</TableCell>
                    <TableCell className="text-right tabular-nums">{count(tier.held)}</TableCell>
                    <TableCell className="text-right tabular-nums">{count(tier.remaining)}</TableCell>
                    <TableCell className="text-right tabular-nums">{formatVnd(tier.revenue)}</TableCell>
                    <TableCell>
                      <Progress value={percent} aria-label={`${tier.name}: đã bán ${percent}%`} />
                    </TableCell>
                  </TableRow>
                );
              })}
            </TableBody>
          </Table>
        </CardContent>
      </Card>

      <div className="grid gap-6 lg:grid-cols-2">
        <Card>
          <CardHeader>
            <CardTitle>Sơ đồ ghế</CardTitle>
          </CardHeader>
          <CardContent>
            <SeatHeatmap eventId={eventId} tiers={snapshot.tiers} />
          </CardContent>
        </Card>
        <Card>
          <CardHeader className="flex flex-row items-center justify-between gap-3">
            <CardTitle>Đơn hàng mới nhất</CardTitle>
            <Button type="button" variant="outline" size="sm" onClick={exportCsv} disabled={exporting}>
              <Download aria-hidden />
              Xuất CSV
            </Button>
          </CardHeader>
          <CardContent className="flex flex-col gap-2 overflow-x-auto">
            {exportError ? (
              <p role="alert" className="text-sm text-danger">
                {exportError}
              </p>
            ) : null}
            {snapshot.recentOrders.length === 0 ? (
              <p className="text-sm text-muted-foreground">Chưa có đơn hàng nào.</p>
            ) : (
              <Table>
                <TableHeader>
                  <TableRow>
                    <TableHead>Người mua</TableHead>
                    <TableHead className="text-right">Số vé</TableHead>
                    <TableHead className="text-right">Tổng</TableHead>
                    <TableHead className="text-right">Lúc</TableHead>
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {snapshot.recentOrders.map((order) => (
                    <TableRow key={order.orderId}>
                      <TableCell className="max-w-48 truncate">{order.buyerName}</TableCell>
                      <TableCell className="text-right tabular-nums">{order.tickets}</TableCell>
                      <TableCell className="text-right tabular-nums">{formatVnd(order.total)}</TableCell>
                      <TableCell className="text-right tabular-nums">{timeOf(order.paidAt)}</TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
            )}
          </CardContent>
        </Card>
      </div>
    </div>
  );
}

function LiveBadge({ live, at }: { live: boolean; at: string }) {
  return (
    <p role="status" className="flex items-center gap-2 text-sm">
      {live ? (
        <Badge className="gap-1.5 bg-success text-white">
          <Radio aria-hidden className="size-3.5 motion-safe:animate-pulse" />
          Live
        </Badge>
      ) : (
        <Badge variant="secondary" className="gap-1.5">
          <WifiOff aria-hidden className="size-3.5" />
          Đang kết nối lại…
        </Badge>
      )}
      <span className="text-muted-foreground">Cập nhật lúc {timeOf(at)}</span>
    </p>
  );
}

function Kpi({ label, value, format }: { label: string; value: number; format: (n: number) => string }) {
  return (
    <Card className="gap-1 py-4">
      <CardContent className="flex flex-col gap-1 px-4">
        <span className="text-xs font-medium text-muted-foreground">{label}</span>
        <AnimatedNumber value={value} format={format} />
      </CardContent>
    </Card>
  );
}

const reducedMotion = () => typeof window !== "undefined" && window.matchMedia?.("(prefers-reduced-motion: reduce)").matches;

/** Counts up to a new value in 400 ms; jumps straight there for people who asked for reduced motion. */
export function AnimatedNumber({ value, format }: { value: number; format: (n: number) => string }) {
  const [shown, setShown] = useState(value);
  const from = useRef(value);

  useEffect(() => {
    const start = from.current;
    from.current = value;
    if (start === value) return;
    const duration = reducedMotion() ? 0 : 400;

    let frame = 0;
    const began = performance.now();
    const step = (now: number) => {
      const progress = duration === 0 ? 1 : Math.min(1, (now - began) / duration);
      setShown(Math.round(start + (value - start) * progress));
      if (progress < 1) frame = requestAnimationFrame(step);
    };
    frame = requestAnimationFrame(step);
    return () => cancelAnimationFrame(frame);
  }, [value]);

  return <span className="text-xl font-bold tabular-nums md:text-2xl">{format(shown)}</span>;
}

const STEPS: { key: keyof DashboardFunnel; label: string }[] = [
  { key: "queueJoined", label: "Vào hàng chờ" },
  { key: "admitted", label: "Được vào mua" },
  { key: "held", label: "Giữ chỗ" },
  { key: "ordersCreated", label: "Tạo đơn" },
  { key: "paid", label: "Đã thanh toán" },
];

/** Each step as a bar against the first, with the drop-off from the step before. */
export function Funnel({ funnel }: { funnel: DashboardFunnel }) {
  const top = Math.max(1, ...STEPS.map((s) => funnel[s.key]));
  return (
    <ol className="flex flex-col gap-3">
      {STEPS.map((step, i) => {
        const value = funnel[step.key];
        const previous = i > 0 ? funnel[STEPS[i - 1]!.key] : 0;
        const drop = i > 0 && previous > 0 ? Math.max(0, Math.round((1 - value / previous) * 100)) : null;
        return (
          <li key={step.key} className="flex flex-col gap-1">
            <div className="flex items-baseline justify-between gap-2 text-sm">
              <span>{step.label}</span>
              <span className="tabular-nums">
                {count(value)}
                {drop !== null ? <span className="ml-2 text-xs text-muted-foreground">−{drop}%</span> : null}
              </span>
            </div>
            <div className="h-2.5 rounded-full bg-muted">
              <div className={cn("h-full rounded-full bg-primary")} style={{ width: `${(value / top) * 100}%` }} />
            </div>
          </li>
        );
      })}
    </ol>
  );
}

function DashboardSkeleton() {
  return (
    <div aria-busy className="flex flex-col gap-6">
      <Skeleton className="h-9 w-72" />
      <div className="grid grid-cols-2 gap-3 md:grid-cols-5">
        {Array.from({ length: 5 }, (_, i) => (
          <Skeleton key={i} className="h-20" />
        ))}
      </div>
      <Skeleton className="h-72 w-full" />
    </div>
  );
}
