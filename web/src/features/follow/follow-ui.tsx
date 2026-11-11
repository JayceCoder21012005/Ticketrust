import { Bell } from "lucide-react";
import Link from "next/link";
import { Button } from "@/components/ui/button";
import { t } from "@/lib/i18n";
import { cn } from "@/lib/utils";

export type FollowSize = "default" | "lg";

/**
 * `/login?returnUrl=…`, as `loginRedirectUrl` in `lib/api/session` builds it. Written out here because that module (token
 * refresh and all) is not in the public pages' JS, and this island is: a test keeps the two identical.
 */
export const loginHref = (path: string) => `/login?returnUrl=${encodeURIComponent(path)}`;

/** What a visitor without a session sees: "Nhắc tôi" is a link to login, which brings them back to this page. */
export function RemindLink({ path, size, className }: { path: string; size: FollowSize; className?: string }) {
  return (
    <Button asChild variant="outline" size={size} className={className}>
      {/* No prefetch, like the header's login link: most visitors never open it. */}
      <Link href={loginHref(path)} prefetch={false}>
        <Bell aria-hidden />
        {t("follow.remind")}
      </Link>
    </Button>
  );
}

/** Stands in for the button while its code and the follow state load: same size, so nothing jumps. */
export function RemindPlaceholder({ size, className }: { size: FollowSize; className?: string }) {
  return (
    <Button type="button" variant="outline" size={size} disabled aria-busy="true" title={t("follow.loading")} className={cn("animate-pulse", className)}>
      <Bell aria-hidden />
      {t("follow.remind")}
    </Button>
  );
}
