"use client";

import { useCallback, useEffect, useReducer, useRef } from "react";
import { getAccessToken, refreshAccessToken } from "@/lib/api/session";
import { clockOffsetMs } from "@/lib/clock";
import { ApiError } from "@/lib/errors";
import { joinQueue, leaveQueue } from "./api";
import { clearAdmission, saveAdmission } from "./admission-store";
import { createQueueTransport, type QueueTransport, type TransportHandlers } from "./transport";
import { TERMINAL_STATES, type QueueStatus } from "./types";

export type QueuePhase = "idle" | "joining" | "active" | "failed";

export type QueueView = {
  phase: QueuePhase;
  status: QueueStatus | null;
  /** First waiting position seen in this visit: the 100% of the progress bar. */
  startPosition: number | null;
  /** Server clock minus device clock, for countdowns (C10). */
  offsetMs: number;
  error: ApiError | null;
  /** Consecutive failed polls; the page shows a "reconnecting" hint from the first one. */
  connectionIssues: number;
};

type Action =
  | { type: "joining" }
  | { type: "status"; status: QueueStatus; offsetMs: number }
  | { type: "failed"; error: ApiError }
  | { type: "issues"; failures: number }
  | { type: "idle" };

const initial: QueueView = { phase: "idle", status: null, startPosition: null, offsetMs: 0, error: null, connectionIssues: 0 };

function reducer(state: QueueView, action: Action): QueueView {
  switch (action.type) {
    case "joining":
      return { ...state, phase: "joining", error: null };
    case "status":
      return {
        ...state,
        phase: "active",
        status: action.status,
        offsetMs: action.offsetMs,
        error: null,
        connectionIssues: 0,
        startPosition: state.startPosition ?? action.status.position,
      };
    case "failed":
      return { ...state, phase: "failed", error: action.error };
    case "issues":
      return { ...state, connectionIssues: action.failures };
    case "idle":
      return initial;
  }
}

type Deps = {
  createTransport?: (handlers: TransportHandlers) => QueueTransport;
  join?: (eventId: string, accessCode?: string, captchaToken?: string | null) => Promise<QueueStatus>;
  leave?: (eventId: string) => Promise<void>;
  getToken?: () => string | null;
  refresh?: () => Promise<string | null>;
};

/**
 * Drives the waiting room for one event: join (idempotent), then follow the server-paced polls made by a worker.
 * Admission is stored for the seat pages and the global shopping timer.
 */
export function useQueue(eventId: string, slug: string, deps: Deps = {}) {
  const { createTransport = createQueueTransport, join = joinQueue, leave = leaveQueue, getToken = getAccessToken, refresh = refreshAccessToken } = deps;
  const [view, dispatch] = useReducer(reducer, initial);
  const transport = useRef<QueueTransport | null>(null);
  // False once the page is left: a join that was still in flight must not start a worker nobody will ever stop.
  const mounted = useRef(true);

  const apply = useCallback(
    (status: QueueStatus) => {
      dispatch({ type: "status", status, offsetMs: clockOffsetMs(status.serverNow, Date.now()) });
      if (status.status === "Admitted" && status.admissionToken && status.tokenExpiresAt) {
        saveAdmission({ eventId, slug, token: status.admissionToken, expiresAt: status.tokenExpiresAt });
      } else if (status.status === "Expired" || status.status === "Closed") {
        clearAdmission(eventId);
      }
    },
    [eventId, slug],
  );

  const ensureTransport = useCallback((): QueueTransport => {
    transport.current ??= createTransport({
      onStatus: apply,
      onUnauthorized: () => {
        void refresh().then((token) => {
          if (token) {
            transport.current?.setToken(token);
          } else {
            dispatch({ type: "failed", error: new ApiError(401, { code: "auth.refresh_invalid" }) });
          }
        });
      },
      onNotJoined: () => dispatch({ type: "failed", error: new ApiError(404, { code: "queue.not_joined" }) }),
      onError: (failures) => dispatch({ type: "issues", failures }),
    });
    return transport.current;
  }, [apply, createTransport, refresh]);

  const stop = useCallback(() => {
    transport.current?.stop();
    transport.current = null;
  }, []);

  const doJoin = useCallback(
    async (accessCode?: string, captchaToken?: string | null) => {
      dispatch({ type: "joining" });
      try {
        const status = await join(eventId, accessCode, captchaToken);
        if (!mounted.current) {
          return;
        }

        apply(status);
        const token = getToken();
        if (token && !TERMINAL_STATES.includes(status.status)) {
          ensureTransport().start(eventId, token);
        }
      } catch (error) {
        dispatch({ type: "failed", error: error instanceof ApiError ? error : new ApiError(0, { code: "system.busy" }) });
      }
    },
    [apply, ensureTransport, eventId, getToken, join],
  );

  const doLeave = useCallback(async () => {
    try {
      await leave(eventId);
    } finally {
      stop();
      clearAdmission(eventId);
      dispatch({ type: "idle" });
    }
  }, [eventId, leave, stop]);

  useEffect(() => {
    mounted.current = true;
    return () => {
      mounted.current = false;
      stop();
    };
  }, [stop]);

  // Coming back to a tab that sat in the background: don't wait out the (possibly long) timer.
  useEffect(() => {
    const onVisible = () => document.visibilityState === "visible" && transport.current?.pollNow();
    document.addEventListener("visibilitychange", onVisible);
    return () => document.removeEventListener("visibilitychange", onVisible);
  }, []);

  return { ...view, join: doJoin, leave: doLeave };
}
