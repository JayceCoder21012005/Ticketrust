"use client";

import { AlertTriangle, RotateCw } from "lucide-react";
import { Button } from "@/components/ui/button";
import { t } from "@/lib/i18n";
import { cn } from "@/lib/utils";

type ErrorStateProps = {
  title?: string;
  /** Friendly message, usually from `toFriendlyMessage(error)`. */
  description?: string;
  onRetry?: () => void;
  className?: string;
};

export function ErrorState({
  title = t("states.errorTitle"),
  description = t("states.errorDescription"),
  onRetry,
  className,
}: ErrorStateProps) {
  return (
    <div
      role="alert"
      className={cn("flex flex-col items-center gap-3 rounded-xl border border-danger/30 bg-danger/5 px-6 py-12 text-center", className)}
    >
      <AlertTriangle aria-hidden className="size-10 text-danger" />
      <h2 className="text-lg font-semibold">{title}</h2>
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
