"use client";

import { useEffect, useMemo, useState, useSyncExternalStore } from "react";
import { ErrorState } from "@/components/shared/error-state";
import { Skeleton } from "@/components/ui/skeleton";
import { formatClock } from "@/lib/format";
import { t } from "@/lib/i18n";
import { qrPath, qrViewBoxSize } from "@/lib/tickets/qr-svg";
import { ProgressRing } from "./progress-ring";
import { useRotatingCode } from "./use-rotating-code";

type Encoder = (text: string) => boolean[][];

/**
 * The QR encoder (~10 KB with its tables) is loaded on demand, by this screen only: it never reaches the shared or
 * public bundles (a test walks the page's static imports).
 */
function useEncoder(): { encode: Encoder | null; failed: boolean } {
  const [state, setState] = useState<{ encode: Encoder | null; failed: boolean }>({ encode: null, failed: false });

  useEffect(() => {
    let live = true;
    import("@/lib/tickets/qr-matrix").then(
      (module) => live && setState({ encode: module.encodeQr, failed: false }),
      () => live && setState({ encode: null, failed: true }),
    );
    return () => {
      live = false;
    };
  }, []);

  return state;
}

const REDUCED_MOTION = "(prefers-reduced-motion: reduce)";
const subscribeMotion = (notify: () => void) => {
  const media = window.matchMedia?.(REDUCED_MOTION);
  media?.addEventListener("change", notify);
  return () => media?.removeEventListener("change", notify);
};
const prefersReducedMotion = () => window.matchMedia?.(REDUCED_MOTION).matches ?? false;

/**
 * The moving band under the QR: a screenshot freezes it, so staff can tell a live screen from a picture. Reduced
 * motion stops it (here and in globals.css); the live clock below keeps proving the screen is live.
 */
function GradientBand() {
  const reduced = useSyncExternalStore(subscribeMotion, prefersReducedMotion, () => true);
  return <div aria-hidden data-testid="wallet-band" data-animated={String(!reduced)} className="wallet-band h-3 w-full rounded-full" />;
}

/** `HH:mm:ss` in Vietnam time on the server-corrected clock. A timer, not a live region: it is never read out by itself. */
function LiveClock({ nowMs }: { nowMs: number }) {
  return (
    <p className="text-center">
      <span className="sr-only">{`${t("tickets.wallet.clock")}: `}</span>
      <span role="timer" aria-live="off" className="text-3xl font-bold tracking-wider tabular-nums">
        {formatClock(nowMs)}
      </span>
    </p>
  );
}

/** Black on white whatever the theme (scanners need the contrast), drawn as one path with crisp module edges. */
function QrSymbol({ payload }: { payload: string | null }) {
  const { encode, failed } = useEncoder();
  const matrix = useMemo(() => (encode && payload ? encode(payload) : null), [encode, payload]);

  if (failed) {
    return <p className="grid size-full place-items-center p-6 text-center text-sm text-neutral-900">{t("tickets.wallet.qrEncoderFailed")}</p>;
  }
  if (!matrix) {
    return (
      <div className="relative size-full">
        <Skeleton className="size-full bg-neutral-200" />
        <span className="sr-only">{t("tickets.wallet.qrLoading")}</span>
      </div>
    );
  }

  const size = qrViewBoxSize(matrix);
  return (
    <svg
      role="img"
      aria-label={t("tickets.wallet.qrLabel")}
      viewBox={`0 0 ${size} ${size}`}
      shapeRendering="crispEdges"
      className="block size-full"
      style={{ forcedColorAdjust: "none" }}
    >
      <rect width={size} height={size} fill="#ffffff" />
      <path data-modules="" d={qrPath(matrix)} fill="#000000" />
    </svg>
  );
}

type RotatingQrProps = {
  ticketId: string;
  /** The ticket's `qr-key` secret (base64). Stays in memory: passed to Web Crypto only. */
  secret: string;
  /** Server − device clock (C10). */
  offsetMs: number;
};

/**
 * The live part of the wallet (spec 10): the QR redrawn on every 15-second step, the ring counting down to the next
 * one, the moving band and the clock. The code itself never appears as text in the page.
 */
export function RotatingQr({ ticketId, secret, offsetMs }: RotatingQrProps) {
  const code = useRotatingCode(ticketId, secret, offsetMs);

  if (code.error === "insecure") {
    return <ErrorState title={t("tickets.wallet.insecure.title")} description={t("tickets.wallet.insecure.description")} />;
  }
  if (code.error === "failed") {
    return <ErrorState title={t("tickets.wallet.codeFailed.title")} description={t("tickets.wallet.codeFailed.description")} />;
  }

  return (
    <div className="flex w-full flex-col items-center gap-4">
      <div className="aspect-square w-full max-w-80 overflow-hidden rounded-2xl bg-white shadow-sm ring-1 ring-black/10">
        <QrSymbol payload={code.payload} />
      </div>
      <div className="flex w-full max-w-80 items-center gap-3">
        <ProgressRing msLeft={code.msLeft} />
        <p className="text-sm text-muted-foreground">{t("tickets.wallet.rotates")}</p>
      </div>
      <div className="flex w-full max-w-80 flex-col items-center gap-2">
        <GradientBand />
        <LiveClock nowMs={code.nowMs} />
      </div>
    </div>
  );
}
