"use client";

import { CircleUser } from "lucide-react";
import Link from "next/link";
import { useSyncExternalStore } from "react";
import { Button } from "@/components/ui/button";
import { hasSessionHint } from "@/lib/auth/session-hint";
import { t } from "@/lib/i18n";

const noSubscription = () => () => {};

/**
 * Tiny island instead of the full AuthProvider, keeping public pages' JS small: the non-secret
 * `tr_session` hint is enough to choose between "Đăng nhập" and the account link.
 */
export function AccountLink() {
  const signedIn = useSyncExternalStore(noSubscription, () => hasSessionHint(document.cookie), () => false);

  if (signedIn) {
    return (
      <Button asChild size="sm" variant="ghost">
        <Link href="/account">
          <CircleUser aria-hidden />
          {t("common.account")}
        </Link>
      </Button>
    );
  }

  return (
    <Button asChild size="sm">
      <Link href="/login">{t("common.login")}</Link>
    </Button>
  );
}
