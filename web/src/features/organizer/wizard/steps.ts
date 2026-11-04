import type { OrganizerEvent, PublishChecklist, WizardStep } from "../types";

export const STEPS: readonly WizardStep[] = ["info", "tiers", "layout", "phases", "review"];

const LABELS: Record<WizardStep, string> = {
  info: "Thông tin",
  tiers: "Hạng vé",
  layout: "Sơ đồ",
  phases: "Mở bán",
  review: "Kiểm tra & Xuất bản",
};

export const stepLabel = (step: WizardStep) => LABELS[step];

export function isStep(value: string): value is WizardStep {
  return (STEPS as readonly string[]).includes(value);
}

export type StepStatus = "done" | "error" | "todo";

/**
 * ✓ when the step has data and no failing rule, ! when a publish rule points at it, ○ otherwise. The checklist only
 * gates publishing: once an event is published its inventory is locked, so no rule can mark a step as failing.
 */
export function stepStatus(step: WizardStep, checklist: PublishChecklist | undefined, event: OrganizerEvent): StepStatus {
  const draft = event.status === "Draft";
  if (step === "review") {
    return !draft || checklist?.ready ? "done" : "todo";
  }

  if (draft && checklist?.failures.some((failure) => failure.step === step)) {
    return "error";
  }

  const filled = {
    info: event.coverImageUrl !== null,
    tiers: event.tiers.length > 0,
    layout: event.layout !== null,
    phases: event.phases.length > 0,
  }[step];

  return filled ? "done" : "todo";
}
