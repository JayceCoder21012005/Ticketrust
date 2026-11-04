import { Bell, Hourglass } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Progress } from "@/components/ui/progress";
import { t } from "@/lib/i18n";
import { etaLabel, formatPosition, progressPercent } from "@/lib/queue/format";
import type { QueueStatus } from "@/lib/queue/types";

type WaitingStateProps = {
  status: QueueStatus;
  /** First position seen: the start of the progress bar. */
  startPosition: number | null;
  connectionIssues: number;
  /** Browser notification permission; the button shows only while it is undecided. */
  notification?: NotificationPermission | "unsupported";
  onEnableNotification?: () => void;
};

export function WaitingState({ status, startPosition, connectionIssues, notification = "unsupported", onEnableNotification }: WaitingStateProps) {
  const position = status.position ?? 1;
  const ahead = status.aheadOfYou ?? Math.max(0, position - 1);

  return (
    <div className="flex flex-col items-center gap-5 text-center">
      <Hourglass aria-hidden className="size-9 text-primary" />
      <div className="flex flex-col gap-1">
        <h2 className="text-xl font-semibold">{t("queue.waiting.title")}</h2>
        <p className="text-2xl font-bold tabular-nums">
          {ahead > 0 ? t("queue.waiting.ahead", "vi", { count: ahead.toLocaleString("vi-VN") }) : t("queue.waiting.aheadNone")}
        </p>
        <p className="text-sm text-muted-foreground">{t("queue.waiting.position", "vi", { position: formatPosition(position) })}</p>
      </div>

      <Progress
        value={progressPercent(startPosition, position)}
        aria-label={t("queue.waiting.progress")}
        className="h-2 w-full"
      />

      {status.estimatedWaitSeconds !== null ? (
        <p className="text-sm font-medium">{t("queue.waiting.eta", "vi", { eta: etaLabel(status.estimatedWaitSeconds) })}</p>
      ) : null}

      <p className="text-sm text-muted-foreground">{t("queue.waiting.noReload")}</p>

      {connectionIssues > 0 ? (
        <p role="status" className="rounded-md bg-warning/15 px-3 py-2 text-sm">
          {t("queue.waiting.reconnecting")}
        </p>
      ) : null}

      {notification === "default" && onEnableNotification ? (
        <Button variant="outline" size="sm" onClick={onEnableNotification}>
          <Bell aria-hidden />
          {t("queue.waiting.notify")}
        </Button>
      ) : null}
      {notification === "granted" ? <p className="text-xs text-muted-foreground">{t("queue.waiting.notifyOn")}</p> : null}
    </div>
  );
}
