import { CircleCheck, CircleDot, CircleX, Circle, type LucideIcon } from "lucide-react";
import { buildTimeline, type TimelineStep } from "@/lib/checkout/order-timeline";
import type { Order } from "@/lib/checkout/types";
import { formatDateTime } from "@/lib/format";
import { t } from "@/lib/i18n";
import { cn } from "@/lib/utils";

/** The shape tells the state (check, dot, empty ring, cross); the colour only reinforces it, and the words are read aloud. */
function markerOf(step: TimelineStep): { icon: LucideIcon; tone: string } {
  if (step.problem) return { icon: CircleX, tone: "text-danger" };
  if (step.state === "done") return { icon: CircleCheck, tone: "text-success" };
  if (step.state === "current") return { icon: CircleDot, tone: "text-primary" };
  return { icon: Circle, tone: "text-muted-foreground" };
}

function Step({ step }: { step: TimelineStep }) {
  const { icon: Icon, tone } = markerOf(step);
  const current = step.state === "current";
  return (
    <li aria-current={current ? "step" : undefined} className="flex gap-3">
      <Icon aria-hidden className={cn("mt-0.5 size-5 shrink-0", tone)} />
      <div className="flex min-w-0 flex-col gap-0.5">
        <p className={cn("text-sm", current ? "font-semibold" : step.state === "upcoming" ? "text-muted-foreground" : "font-medium")}>
          <span data-step-title>{t(`orders.timeline.steps.${step.key}`)}</span>
          {" "}
          <span className="sr-only">{`(${t(`orders.timeline.state.${step.state}`)})`}</span>
        </p>
        {step.at ? (
          <time dateTime={step.at} className="text-xs text-muted-foreground">
            {step.atIsDeadline ? t("orders.timeline.deadline", "vi", { time: formatDateTime(step.at) }) : formatDateTime(step.at)}
          </time>
        ) : null}
        {step.notes.map((note) => (
          <p key={note} className="text-sm text-muted-foreground">
            {t(note)}
          </p>
        ))}
      </div>
    </li>
  );
}

/** The path of one order, derived from its status and the timestamps the api returns (nothing is invented). */
export function StatusTimeline({ order, headingId }: { order: Order; headingId: string }) {
  return (
    <section aria-labelledby={headingId} className="flex flex-col gap-3">
      <h2 id={headingId} className="text-base font-semibold">
        {t("orders.detail.timeline")}
      </h2>
      <ol aria-label={t("orders.timeline.label")} className="flex flex-col gap-4">
        {buildTimeline(order).map((step) => (
          <Step key={step.key} step={step} />
        ))}
      </ol>
    </section>
  );
}
