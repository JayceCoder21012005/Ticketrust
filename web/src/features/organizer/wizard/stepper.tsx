import { Check, Circle, CircleAlert } from "lucide-react";
import Link from "next/link";
import { cn } from "@/lib/utils";
import type { OrganizerEvent, PublishChecklist, WizardStep } from "../types";
import { STEPS, stepLabel, stepStatus, type StepStatus } from "./steps";

const STATUS = {
  done: { icon: Check, label: "Đã xong", className: "bg-success text-white" },
  error: { icon: CircleAlert, label: "Cần sửa", className: "bg-danger text-white" },
  todo: { icon: Circle, label: "Chưa làm", className: "border text-muted-foreground" },
} satisfies Record<StepStatus, unknown>;

/** Left stepper on desktop, a horizontal strip on phones (design §3.3). */
export function Stepper({ eventId, current, event, checklist }: { eventId: string; current: WizardStep; event: OrganizerEvent; checklist?: PublishChecklist }) {
  return (
    <nav aria-label="Các bước tạo sự kiện">
      <ol className="flex gap-2 overflow-x-auto pb-1 md:flex-col md:gap-1 md:overflow-visible">
        {STEPS.map((step, i) => {
          const status = STATUS[stepStatus(step, checklist, event)];
          const active = step === current;
          return (
            <li key={step} className="shrink-0">
              <Link
                href={`/organizer/events/${eventId}/${step}`}
                prefetch={false}
                aria-current={active ? "step" : undefined}
                className={cn(
                  "flex items-center gap-3 rounded-lg px-3 py-2 text-sm hover:bg-muted",
                  active && "bg-muted font-semibold text-foreground",
                )}
              >
                <span className={cn("flex size-6 shrink-0 items-center justify-center rounded-full text-xs", status.className)}>
                  <status.icon aria-hidden className="size-3.5" />
                </span>
                <span>
                  <span className="text-muted-foreground">{i + 1}. </span>
                  {stepLabel(step)}
                  <span className="sr-only"> ({status.label})</span>
                </span>
              </Link>
            </li>
          );
        })}
      </ol>
    </nav>
  );
}
