"use client";

import { useEffect, useState } from "react";
import { toast } from "sonner";
import { ApiError } from "@/lib/errors";
import { t } from "@/lib/i18n";

/** Seconds a 429 asks to wait: `retryAfterSeconds` from the body (spec 12 always sends it), else 0. */
export function retryAfterOf(error: unknown): number | null {
  if (!(error instanceof ApiError) || error.status !== 429) return null;
  const seconds = Number(error.problem.retryAfterSeconds);
  return Number.isFinite(seconds) && seconds > 0 ? Math.ceil(seconds) : 0;
}

function Countdown({ seconds }: { seconds: number }) {
  const [left, setLeft] = useState(seconds);

  useEffect(() => {
    if (left <= 0) return;
    const timer = setTimeout(() => setLeft((s) => s - 1), 1000);
    return () => clearTimeout(timer);
  }, [left]);

  return <span>{left > 0 ? t("rateLimit.wait", "vi", { seconds: left }) : t("rateLimit.ready")}</span>;
}

/**
 * Spec 12: a 429 becomes a toast that counts down from `Retry-After`. Nothing is retried for the user (never a silent loop):
 * they try again when the toast says they can. Returns `false` for any other error, so the caller shows its own message.
 */
export function showRateLimitToast(error: unknown): boolean {
  const seconds = retryAfterOf(error);
  if (seconds === null) return false;
  toast.warning(t("rateLimit.title"), {
    id: "rate-limited",
    description: <Countdown seconds={seconds} />,
    duration: (seconds + 2) * 1000,
  });
  return true;
}
