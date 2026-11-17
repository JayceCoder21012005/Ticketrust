"use client";

import { useQuery, useQueryClient } from "@tanstack/react-query";
import { ArrowLeft, CameraOff, FlipHorizontal2, Flashlight, FlashlightOff, WifiOff } from "lucide-react";
import Link from "next/link";
import { useCallback, useEffect, useRef, useState } from "react";
import { Button } from "@/components/ui/button";
import { describeFailure, describeScan, type ScanView } from "@/lib/gate/describe-scan";
import { getGateStats, listGateEvents, scanTicket } from "@/lib/gate/api";
import { giveFeedback } from "@/lib/gate/feedback";
import { cn } from "@/lib/utils";
import { ManualEntry } from "./manual-entry";
import { ResultOverlay } from "./result-overlay";
import { useCamera } from "./use-camera";
import { useOnline } from "./use-online";
import { useScanLoop } from "./use-scan-loop";
import { useWakeLock } from "./use-wake-lock";

/** The result stays this long over the camera, unless the staff member taps it away. */
const RESULT_MS = 1500;
const STATS_REFRESH_MS = 5000;

const numbers = new Intl.NumberFormat("vi-VN");

/** The full-screen scanner of one event (spec 11). */
export function ScannerScreen({ eventId }: { eventId: string }) {
  const queryClient = useQueryClient();
  const online = useOnline();
  const videoRef = useRef<HTMLVideoElement>(null);
  const camera = useCamera(videoRef);
  const [view, setView] = useState<ScanView | null>(null);
  const busy = useRef(false);
  const hideTimer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);

  useWakeLock();

  const events = useQuery({ queryKey: ["gate", "events"], queryFn: ({ signal }) => listGateEvents(signal) });
  const stats = useQuery({
    queryKey: ["gate", "stats", eventId],
    queryFn: ({ signal }) => getGateStats(eventId, signal),
    refetchInterval: STATS_REFRESH_MS,
    retry: false,
  });
  const gateName = events.data?.find((e) => e.eventId === eventId)?.gateName;
  const title = events.data?.find((e) => e.eventId === eventId)?.title;
  const connected = online && !stats.isError;

  const show = useCallback((next: ScanView) => {
    clearTimeout(hideTimer.current);
    setView(next);
    giveFeedback(next.tone);
    hideTimer.current = setTimeout(() => setView(null), RESULT_MS);
  }, []);

  useEffect(() => () => clearTimeout(hideTimer.current), []);

  const submit = useCallback(
    async (payload: string) => {
      if (busy.current) return;
      busy.current = true;
      try {
        const response = await scanTicket(eventId, payload);
        show(describeScan(response));
        if (response.result === "Admitted") void queryClient.invalidateQueries({ queryKey: ["gate", "stats", eventId] });
      } catch (error) {
        show(describeFailure(error));
      } finally {
        busy.current = false;
      }
    },
    [eventId, queryClient, show],
  );

  useScanLoop({ videoRef, active: camera.status === "ready", paused: view !== null, onCode: submit });

  return (
    <div className="flex min-h-dvh flex-col bg-black text-white">
      <header className="flex items-center gap-3 border-b border-white/15 p-3">
        <Link href="/gate" aria-label="Về danh sách sự kiện" className="rounded-md p-2 hover:bg-white/10">
          <ArrowLeft aria-hidden className="size-5" />
        </Link>
        <div className="min-w-0 flex-1">
          <p className="truncate text-sm font-semibold">{gateName ?? "Cổng"}</p>
          <p className="truncate text-xs text-white/70">{title ?? ""}</p>
        </div>
        <p aria-label="Số vé đã vào" className="text-right text-lg font-bold tabular-nums" data-testid="gate-counter">
          {stats.data ? `${numbers.format(stats.data.checkedIn)} / ${numbers.format(stats.data.total)}` : "– / –"}
        </p>
        <span
          role="status"
          className={cn("flex items-center gap-1 rounded-full px-2 py-1 text-xs font-medium", connected ? "bg-green-900 text-green-100" : "bg-red-900 text-red-100")}
        >
          <span aria-hidden className={cn("size-2 rounded-full", connected ? "bg-green-400" : "bg-red-400")} />
          {connected ? "Đã kết nối" : "Mất kết nối"}
        </span>
      </header>

      {!connected ? (
        <div role="alert" className="flex items-center justify-center gap-2 bg-red-700 p-2 text-sm font-medium">
          <WifiOff aria-hidden className="size-4" />
          Mất kết nối — vé quét lúc này chưa được kiểm tra
        </div>
      ) : null}

      <div className="relative flex-1 bg-neutral-900">
        {/* The video element exists from the start: the camera attaches its stream to it. */}
        <video ref={videoRef} playsInline muted className={cn("absolute inset-0 size-full object-cover", camera.status !== "ready" && "invisible")} />
        {camera.status === "ready" ? <ScanFrame /> : <CameraNotice status={camera.status} />}
        {camera.status === "ready" ? (
          <div className="absolute inset-x-0 bottom-4 flex justify-center gap-3">
            {camera.torchSupported ? (
              <Button type="button" variant="secondary" size="lg" onClick={() => void camera.toggleTorch()} aria-pressed={camera.torchOn}>
                {camera.torchOn ? <FlashlightOff aria-hidden /> : <Flashlight aria-hidden />}
                {camera.torchOn ? "Tắt đèn" : "Bật đèn"}
              </Button>
            ) : null}
            <Button type="button" variant="secondary" size="lg" onClick={camera.switchCamera}>
              <FlipHorizontal2 aria-hidden />
              Đổi camera
            </Button>
          </div>
        ) : null}
      </div>

      <div className="border-t border-white/15 p-3">
        <ManualEntry onSubmit={(payload) => void submit(payload)} disabled={view !== null} />
      </div>

      {view ? <ResultOverlay view={view} onClose={() => setView(null)} /> : null}
    </div>
  );
}

function ScanFrame() {
  return (
    <div aria-hidden className="pointer-events-none absolute inset-0 flex items-center justify-center">
      <div className="size-64 rounded-3xl border-4 border-white/80 shadow-[0_0_0_9999px_rgba(0,0,0,0.35)]" />
    </div>
  );
}

function CameraNotice({ status }: { status: "starting" | "denied" | "unavailable" | "ready" }) {
  if (status === "starting") {
    return <p className="absolute inset-0 flex items-center justify-center text-white/70">Đang mở camera…</p>;
  }

  return (
    <div role="alert" className="absolute inset-0 flex flex-col items-center justify-center gap-3 p-6 text-center">
      <CameraOff aria-hidden className="size-10 text-white/70" />
      <p className="text-lg font-semibold">{status === "denied" ? "Chưa cho phép dùng camera" : "Không mở được camera"}</p>
      <p className="max-w-sm text-sm text-white/70">
        {status === "denied" ? "Cấp quyền camera cho trang này trong cài đặt trình duyệt, rồi tải lại. " : ""}
        Bạn vẫn có thể nhập mã vé ở bên dưới.
      </p>
    </div>
  );
}
