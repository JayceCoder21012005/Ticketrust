"use client";

import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useState, useSyncExternalStore, type ComponentType } from "react";
import { ErrorState } from "@/components/shared/error-state";
import { Skeleton } from "@/components/ui/skeleton";
import { toFriendlyMessage } from "@/lib/errors";
import { getChecklist, getEvent, organizerKeys } from "../api";
import { OrganizerShell } from "../organizer-shell";
import { StatusBadge } from "../status-badge";
import type { OrganizerEvent, WizardStep } from "../types";
import { eventSaves } from "./event-saves";
import { InfoStep } from "./info/info-step";
import { LayoutStep } from "./layout/layout-step";
import { PhasesStep } from "./phases/phases-step";
import { ReviewStep } from "./review/review-step";
import { SaveIndicator } from "./save-indicator";
import type { StepProps } from "./step-props";
import { Stepper } from "./stepper";
import { stepLabel } from "./steps";
import { TiersStep } from "./tiers/tiers-step";
import type { AutosaveStatus } from "./use-autosave";

const STEP_COMPONENTS: Record<WizardStep, ComponentType<StepProps>> = {
  info: InfoStep,
  tiers: TiersStep,
  layout: LayoutStep,
  phases: PhasesStep,
  review: ReviewStep,
};

/**
 * The wizard frame: loads the event, hosts one step at a time, and stores each save's response in the event's save
 * queue (the version the next save sends) and the query cache (what the steps show).
 */
export function WizardView({ eventId, step }: { eventId: string; step: WizardStep }) {
  const queryClient = useQueryClient();
  const event = useQuery({ queryKey: organizerKeys.event(eventId), queryFn: () => getEvent(eventId) });
  const checklist = useQuery({ queryKey: organizerKeys.checklist(eventId), queryFn: () => getChecklist(eventId), enabled: event.isSuccess });
  const [save, setSave] = useState<{ status: AutosaveStatus; errors: number } | null>(null);
  const [generation, setGeneration] = useState(0);
  const saves = eventSaves(queryClient, eventId);
  // A save of the step left before this page mounted can still fail; its step can't say so any more, so the header does.
  const leftFailure = useSyncExternalStore(saves.subscribe, saves.leftFailure, () => null);

  const onSaved = (updated: OrganizerEvent) => {
    saves.saved(updated);
    queryClient.setQueryData(organizerKeys.event(eventId), updated);
    void queryClient.invalidateQueries({ queryKey: organizerKeys.checklist(eventId) });
    void queryClient.invalidateQueries({ queryKey: ["organizer", "events"] });
  };

  const reload = async () => {
    // The unsaved change was made on an outdated event: drop it (no save on leaving, none queued) before reloading.
    save?.status.discard();
    saves.discard();
    await event.refetch();
    void checklist.refetch();
    setSave(null);
    // Remount the step so its form starts again from the reloaded event.
    setGeneration((g) => g + 1);
  };

  const title = event.data?.title ?? "Sự kiện";
  const Step = STEP_COMPONENTS[step];

  return (
    <OrganizerShell
      breadcrumbs={[{ label: "Sự kiện", href: "/organizer/events" }, { label: title, href: `/organizer/events/${eventId}/info` }, { label: stepLabel(step) }]}
      actions={<SaveIndicator status={leftFailure ?? save?.status ?? null} errorCount={save?.errors ?? 0} onReload={() => void reload()} />}
    >
      {event.isPending ? (
        <div className="grid gap-6 p-4 md:grid-cols-[14rem_1fr] md:p-6">
          <Skeleton className="h-48" />
          <Skeleton className="h-96" />
        </div>
      ) : event.isError ? (
        <div className="p-4 md:p-6">
          <ErrorState description={toFriendlyMessage(event.error)} onRetry={() => void event.refetch()} />
        </div>
      ) : (
        <div className="grid gap-6 p-4 md:grid-cols-[14rem_1fr] md:p-6">
          <aside className="md:sticky md:top-4 md:self-start">
            <Stepper eventId={eventId} current={step} event={event.data} checklist={checklist.data} />
          </aside>
          <section aria-labelledby="step-title" className="flex min-w-0 flex-col gap-6">
            <div className="flex flex-wrap items-center gap-3">
              <h1 id="step-title" className="text-2xl font-semibold">
                {stepLabel(step)}
              </h1>
              <StatusBadge status={event.data.status} />
            </div>
            <Step key={`${step}-${generation}`} event={event.data} onSaved={onSaved} onSaveState={(status, errors) => setSave({ status, errors })} />
          </section>
        </div>
      )}
    </OrganizerShell>
  );
}
