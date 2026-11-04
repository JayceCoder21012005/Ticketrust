import { useCallback, useEffect, useRef, useState } from "react";
import { ApiError } from "@/lib/errors";

export type SaveState = "idle" | "saving" | "saved" | "invalid" | "error";

export type AutosaveStatus = {
  state: SaveState;
  savedAt: Date | null;
  /** The api refused a stale version (another tab saved first): reload instead of retrying. */
  conflict: boolean;
  retry: () => void;
  /** Saves now instead of waiting for the debounce, e.g. when leaving the step. */
  flush: () => void;
  /** Forgets the unsaved change (the organizer chose to reload): nothing more is saved, not even on leaving. */
  discard: () => void;
};

type Options<T> = {
  value: T;
  valid: boolean;
  save: (value: T) => Promise<void>;
  delayMs?: number;
  /** The status of a save that finished after the step was left (unmounted), when nothing on the step can show it. */
  onLeftStatus?: (status: AutosaveStatus) => void;
};

/**
 * Wizard autosave (design §3.3): saves a valid value 800 ms after the last change. Saves never overlap — a change made
 * during a save queues exactly one more. The version a save sends is not this hook's business: steps save through the
 * event's queue (`EventSaves`), which hands each save the version the previous one returned.
 */
export function useAutosave<T>({ value, valid, save, delayMs = 800, onLeftStatus }: Options<T>): AutosaveStatus {
  const serialized = JSON.stringify(value);
  const [savedSnapshot, setSavedSnapshot] = useState(serialized);
  const [state, setState] = useState<SaveState>("idle");
  const [savedAt, setSavedAt] = useState<Date | null>(null);
  const [conflict, setConflict] = useState(false);
  const latest = useRef(value);
  const saveRef = useRef(save);
  const inFlight = useRef(false);
  const queued = useRef(false);
  const timer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  const dirty = serialized !== savedSnapshot;

  const pending = useRef(false);
  const conflictRef = useRef(false);
  const discarded = useRef(false);
  const left = useRef(false);
  const onLeftRef = useRef(onLeftStatus);

  useEffect(() => {
    latest.current = value;
    saveRef.current = save;
    onLeftRef.current = onLeftStatus;
    pending.current = dirty && valid && !discarded.current;
    conflictRef.current = conflict;
  });

  const discard = useCallback(() => {
    clearTimeout(timer.current);
    discarded.current = true;
    pending.current = false;
    queued.current = false;
  }, []);

  // After the step is gone its own indicator is too: the frame shows what happened instead (a failure stays there).
  const retryRef = useRef(() => {});
  const reportLeft = useCallback(
    (outcome: Pick<AutosaveStatus, "state" | "conflict">) =>
      onLeftRef.current?.({ ...outcome, savedAt: null, retry: () => retryRef.current(), flush: () => {}, discard }),
    [discard],
  );

  const run = useCallback(async () => {
    clearTimeout(timer.current);
    if (inFlight.current) {
      queued.current = true;
      return;
    }

    inFlight.current = true;
    do {
      queued.current = false;
      const current = latest.current;
      setState("saving");
      let outcome: Pick<AutosaveStatus, "state" | "conflict">;
      try {
        await saveRef.current(current);
        setSavedSnapshot(JSON.stringify(current));
        setSavedAt(new Date());
        outcome = { state: "saved", conflict: false };
      } catch (error) {
        outcome = { state: "error", conflict: error instanceof ApiError && error.code === "event.concurrent_edit" };
        queued.current = false;
      }

      setConflict(outcome.conflict);
      setState(outcome.state);
      conflictRef.current = outcome.conflict;
      if (left.current) {
        reportLeft(outcome);
      }
    } while (queued.current);
    inFlight.current = false;
  }, [reportLeft]);

  // Leaving the step (unmount) saves a pending valid change at once instead of dropping it with the debounce timer.
  // Not after a conflict: that change was made on an outdated event, so saving it again can only fail or overwrite.
  useEffect(() => {
    left.current = false;
    retryRef.current = () => void run();
    return () => {
      left.current = true;
      if (pending.current && conflictRef.current) {
        reportLeft({ state: "error", conflict: true });
      } else if (pending.current) {
        void run();
      }
    };
  }, [run, reportLeft]);

  useEffect(() => {
    if (!dirty || !valid) {
      return;
    }

    timer.current = setTimeout(() => void run(), delayMs);
    return () => clearTimeout(timer.current);
  }, [serialized, dirty, valid, delayMs, run]);

  useEffect(() => {
    if (!dirty) {
      return;
    }

    const warn = (event: BeforeUnloadEvent) => event.preventDefault();
    window.addEventListener("beforeunload", warn);
    return () => window.removeEventListener("beforeunload", warn);
  }, [dirty]);

  return {
    state: dirty && !valid ? "invalid" : state,
    savedAt,
    conflict,
    retry: () => void run(),
    flush: () => {
      if (dirty && valid) {
        void run();
      }
    },
    discard,
  };
}
