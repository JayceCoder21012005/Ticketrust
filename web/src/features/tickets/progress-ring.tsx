import { t } from "@/lib/i18n.buyer";
import { cn } from "@/lib/utils";

const RADIUS = 20;
const CIRCUMFERENCE = 2 * Math.PI * RADIUS;

type ProgressRingProps = {
  /** Time until the next code. */
  msLeft: number;
  totalMs?: number;
  className?: string;
};

/**
 * The 15-second countdown to the next code, as a ring that empties. A progressbar for assistive technology (read on
 * demand, never announced every second). The sweep between ticks is a CSS transition, off under reduced motion.
 */
export function ProgressRing({ msLeft, totalMs = 15_000, className }: ProgressRingProps) {
  const totalSeconds = Math.round(totalMs / 1000);
  const seconds = Math.min(totalSeconds, Math.max(0, Math.ceil(msLeft / 1000)));
  const fraction = Math.min(1, Math.max(0, msLeft / totalMs));
  // A new step refills the ring at once; only the countdown within a step is animated.
  const refilled = seconds === totalSeconds;

  return (
    <div
      role="progressbar"
      aria-label={t("tickets.wallet.ring.label")}
      aria-valuemin={0}
      aria-valuemax={totalSeconds}
      aria-valuenow={seconds}
      aria-valuetext={t("tickets.wallet.ring.value", "vi", { seconds })}
      className={cn("relative size-12 shrink-0", className)}
    >
      <svg aria-hidden viewBox="0 0 48 48" className="size-full -rotate-90">
        <circle cx="24" cy="24" r={RADIUS} fill="none" strokeWidth="4" className="stroke-muted" />
        <circle
          cx="24"
          cy="24"
          r={RADIUS}
          fill="none"
          strokeWidth="4"
          strokeLinecap="round"
          strokeDasharray={CIRCUMFERENCE}
          strokeDashoffset={CIRCUMFERENCE * (1 - fraction)}
          className={cn("stroke-primary", refilled ? "transition-none" : "transition-[stroke-dashoffset] duration-1000 ease-linear motion-reduce:transition-none")}
        />
      </svg>
      <span aria-hidden className="absolute inset-0 grid place-items-center text-sm font-semibold tabular-nums">
        {seconds}
      </span>
    </div>
  );
}
