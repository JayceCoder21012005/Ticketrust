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
        <Link href="/account" title={t("common.account")}>
          <CircleUser aria-hidden />
          {/* Icon only on the narrowest phones: with the bell beside it, the label pushed the header past 320 px. */}
          <span className="sr-only sm:not-sr-only">{t("common.account")}</span>
        </Link>
      </Button>
    );
  }

  return (
    <Button asChild size="sm">
      {/* No prefetch: the header is on every page, and most visitors never open login. Prefetching would
          download the auth forms' JS (react-hook-form, session code) for every anonymous visit. */}
      <Link href="/login" prefetch={false}>
        {t("common.login")}
      </Link>
    </Button>
  );
}
