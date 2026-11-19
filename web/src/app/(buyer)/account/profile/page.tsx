import type { Metadata } from "next";
import { PageHeader } from "@/components/shared/page-header";
import { AccountView } from "@/features/account/account-view";
import { t } from "@/lib/i18n.buyer";

export const metadata: Metadata = { title: t("accountHub.profileTitle"), robots: { index: false } };

/** Profile and avatar (spec 03), now a page of the account hub (UI refresh). */
export default function ProfilePage() {
  return (
    <div className="flex max-w-2xl flex-col gap-8">
      <PageHeader title={t("accountHub.profileTitle")} description={t("accountHub.profileDescription")} />
      <AccountView />
    </div>
  );
}
