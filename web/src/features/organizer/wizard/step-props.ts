import { useQueryClient } from "@tanstack/react-query";
import { useEffect, useRef } from "react";
import type { OrganizerEvent } from "../types";
import { eventSaves } from "./event-saves";
import { useAutosave, type AutosaveStatus } from "./use-autosave";

/** What every wizard step gets from the wizard frame. */
export type StepProps = {
  event: OrganizerEvent;
  /** The api's response to a save: the frame stores it (queue and query cache) so the next save sends the new version. */
  onSaved: (event: OrganizerEvent) => void;
  /** Feeds the header save indicator. */
  onSaveState: (status: AutosaveStatus, errorCount: number) => void;
};

/** Reports the autosave status to the frame when something visible changes (not on every render). */
export function useReportSaveState(status: AutosaveStatus, errorCount: number, report: StepProps["onSaveState"]) {
  const latest = useRef({ status, report });

  useEffect(() => {
    latest.current = { status, report };
  });

  useEffect(() => {
    latest.current.report(latest.current.status, errorCount);
  }, [status.state, status.savedAt, status.conflict, errorCount]);
}

/** The latest event, readable from async save callbacks without re-creating them. */
export function useLatest<T>(value: T) {
  const ref = useRef(value);

  useEffect(() => {
    ref.current = value;
  });

  return ref;
}

type StepAutosave<T> = {
  value: T;
  valid: boolean;
  /** `current` is the newest event this tab knows, read when the save starts: send its version. */
  save: (value: T, current: OrganizerEvent) => Promise<void>;
  errorCount: number;
};

/**
 * A step's autosave: debounced like any form, but every save runs through the event's one queue (see `EventSaves`),
 * so it never overlaps another step's save and always sends the newest version. Reports to the header indicator,
 * including a failure that happens after the step was left.
 */
export function useStepAutosave<T>({ event, onSaveState }: StepProps, { value, valid, save, errorCount }: StepAutosave<T>) {
  const saves = eventSaves(useQueryClient(), event.id);
  const loaded = useLatest(event);
  const autosave = useAutosave({
    value,
    valid,
    save: (v) => saves.run((current) => save(v, current), () => loaded.current),
    onLeftStatus: saves.reportLeft,
  });
  useReportSaveState(autosave, errorCount, onSaveState);
  return autosave;
}
