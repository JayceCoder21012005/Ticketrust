import { Shuffle } from "lucide-react";
import { CountdownTimer } from "@/components/shared/countdown-timer";
import { t } from "@/lib/i18n";
import type { QueueStatus } from "@/lib/queue/types";

/**
 * Before opening: a big countdown on server time and the promise of fairness. Everyone here is shuffled at opening,
 * so there is no place to show yet.
 */
export function PreState({ status }: { status: QueueStatus }) {
  return (
    <div className="flex flex-col items-center gap-5 text-center">
      <Shuffle aria-hidden className="size-9 text-primary" />
      <div className="flex flex-col gap-1">
        <h2 className="text-xl font-semibold">{t("queue.pre.title")}</h2>
        <p className="text-sm text-muted-foreground">{t("queue.pre.opensIn")}</p>
        <CountdownTimer expiresAt={status.opensAt} serverNow={status.serverNow} className="text-5xl" />
      </div>
      <p className="max-w-xs text-sm font-medium">{t("queue.pre.fairness")}</p>
      <p className="max-w-xs text-sm text-muted-foreground">{t("queue.pre.stay")}</p>
    </div>
  );
}
