"use client";

import { AlertTriangle, RotateCw } from "lucide-react";
import type { Ref } from "react";
import { Button } from "@/components/ui/button";
import { t } from "@/lib/i18n";
import { cn } from "@/lib/utils";

type ErrorStateProps = {
  title?: string;
  /** Friendly message, usually from `toFriendlyMessage(error)`. */
  description?: string;
  onRetry?: () => void;
  className?: string;
  /** When given, the title is focusable so a route can move focus to it (the state replaces the page content). */
  headingRef?: Ref<HTMLHeadingElement>;
};

export function ErrorState({
  title = t("states.errorTitle"),
  description = t("states.errorDescription"),
  onRetry,
  className,
  headingRef,
}: ErrorStateProps) {
  return (
    <div
      role="alert"
      className={cn("flex flex-col items-center gap-3 rounded-xl border border-danger/30 bg-danger/5 px-6 py-12 text-center", className)}
    >
      <AlertTriangle aria-hidden className="size-10 text-danger" />
      <h2 ref={headingRef} tabIndex={headingRef ? -1 : undefined} className="text-lg font-semibold outline-none">
        {title}
      </h2>
      <p className="max-w-sm text-sm text-muted-foreground">{description}</p>
      {onRetry ? (
        <Button variant="outline" onClick={onRetry} className="mt-2">
          <RotateCw aria-hidden />
          {t("common.retry")}
        </Button>
      ) : null}
    </div>
  );
}
