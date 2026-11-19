import type { Metadata } from "next";
import { AccountOverview } from "@/features/account/overview";
import { t } from "@/lib/i18n";

export const metadata: Metadata = { title: t("account.title"), robots: { index: false } };

/** The account hub's overview (UI refresh): next event, recent orders, followed events. */
export default function AccountPage() {
  return <AccountOverview />;
}
