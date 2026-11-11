import { Bell } from "lucide-react";
import type { ComponentProps } from "react";
import { Button } from "@/components/ui/button";
import { t } from "@/lib/i18n";

/** The badge never grows past three characters: the exact count is in the accessible name. */
export const badgeText = (count: number) => (count > 99 ? "99+" : String(count));

/**
 * The bell: an icon button with the unread badge. Its accessible name carries the count ("3 thông báo chưa đọc"), so the
 * visible badge is hidden from assistive technology (it would be read twice, and "99+" is less exact).
 */
export function BellButton({ count, ...props }: { count: number } & Omit<ComponentProps<typeof Button>, "children" | "aria-label" | "className">) {
  return (
    <Button variant="ghost" size="icon" aria-label={count > 0 ? t("notifications.unreadLabel", "vi", { count }) : t("notifications.open")} {...props} className="relative">
      <Bell aria-hidden />
      {count > 0 ? (
        <span
          aria-hidden
          className="absolute -right-0.5 -top-0.5 flex h-4 min-w-4 items-center justify-center rounded-full bg-primary px-1 text-[10px] font-bold leading-none text-primary-foreground tabular-nums"
        >
          {badgeText(count)}
        </span>
      ) : null}
    </Button>
  );
}
