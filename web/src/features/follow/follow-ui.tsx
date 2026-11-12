import { Bell, BellRing } from "lucide-react";
import Link from "next/link";
import { Button } from "@/components/ui/button";
import { t } from "@/lib/i18n";
import { cn } from "@/lib/utils";

/** `icon`: the bell alone (the mobile sticky bar, where a label beside "Mua vé" would not fit at 320 px). */
export type FollowSize = "default" | "lg" | "sm" | "icon";
/** `outline`: the main action of the CTA (sale not open yet). `ghost`: a secondary action next to "Mua vé" and the like. */
export type FollowVariant = "outline" | "ghost";

/**
 * `/login?returnUrl=…`, as `loginRedirectUrl` in `lib/api/session` builds it. Written out here because that module (token
 * refresh and all) is not in the public pages' JS, and this island is: a test keeps the two identical.
 */
export const loginHref = (path: string) => `/login?returnUrl=${encodeURIComponent(path)}`;

/**
 * The button's content: bell and label, or for the `icon` size only the bell, with the label as its accessible name and
 * tooltip (the current label, so the state is still announced).
 */
export function FollowFace({ following = false, size }: { following?: boolean; size: FollowSize }) {
  const label = t(following ? "follow.following" : "follow.remind");
  return (
    <>
      {following ? <BellRing aria-hidden /> : <Bell aria-hidden />}
      {size === "icon" ? null : label}
    </>
  );
}

/** `aria-label` and `title` for an icon-only button; nothing for a labelled one (its text is its name). */
export function iconNaming(size: FollowSize, following = false, title?: string) {
  if (size !== "icon") {
    return title ? { title } : {};
  }

  const label = t(following ? "follow.following" : "follow.remind");
  return { "aria-label": label, title: title ?? label };
}

/** What a visitor without a session sees: "Nhắc tôi" is a link to login, which brings them back to this page. */
export function RemindLink({ path, size, variant = "outline", className }: { path: string; size: FollowSize; variant?: FollowVariant; className?: string }) {
  return (
    <Button asChild variant={variant} size={size} className={className}>
      {/* No prefetch, like the header's login link: most visitors never open it. */}
      <Link href={loginHref(path)} prefetch={false} {...iconNaming(size)}>
        <FollowFace size={size} />
      </Link>
    </Button>
  );
}

/** Stands in for the button while its code and the follow state load: same size, so nothing jumps. */
export function RemindPlaceholder({ size, variant = "outline", className }: { size: FollowSize; variant?: FollowVariant; className?: string }) {
  return (
    <Button type="button" variant={variant} size={size} disabled aria-busy="true" {...iconNaming(size, false, t("follow.loading"))} className={cn("animate-pulse", className)}>
      <FollowFace size={size} />
    </Button>
  );
}
