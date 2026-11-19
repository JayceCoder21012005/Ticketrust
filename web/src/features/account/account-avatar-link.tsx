"use client";

import { CircleUser } from "lucide-react";
import Link from "next/link";
import { useSyncExternalStore } from "react";
import { Avatar, AvatarFallback, AvatarImage } from "@/components/ui/avatar";
import { Button } from "@/components/ui/button";
import { getSession, subscribe } from "@/lib/api/session";
import { t } from "@/lib/i18n";

/**
 * The header's account link on buyer pages (UI refresh): the buyer's avatar (or initial) once the session is restored. Public
 * pages keep the tiny AccountLink island: the session module would cost them JS for a picture.
 */
export function AccountAvatarLink() {
  const user = useSyncExternalStore(subscribe, () => getSession()?.user ?? null, () => null);
  return (
    <Button asChild size="sm" variant="ghost" className="gap-2">
      <Link href="/account" title={t("common.account")}>
        {user ? (
          <Avatar aria-hidden className="size-7">
            {user.avatarUrl ? <AvatarImage src={user.avatarUrl} alt="" /> : null}
            <AvatarFallback className="text-xs font-semibold">{user.fullName.charAt(0).toUpperCase()}</AvatarFallback>
          </Avatar>
        ) : (
          <CircleUser aria-hidden />
        )}
        {/* Visible from sm up; on phones it stays the link's name for screen readers. */}
        <span className="sr-only sm:not-sr-only">{t("common.account")}</span>
      </Link>
    </Button>
  );
}
