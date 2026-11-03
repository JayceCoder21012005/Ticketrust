import type { Metadata } from "next";
import { PageHeader } from "@/components/shared/page-header";
import { AccountView } from "@/features/account/account-view";
import { t } from "@/lib/i18n";

export const metadata: Metadata = { title: t("account.title"), robots: { index: false } };

export default function AccountPage() {
  return (
    <div className="mx-auto flex w-full max-w-2xl flex-col gap-8 px-4 py-10">
      <PageHeader title={t("account.title")} description={t("account.description")} />
      <AccountView />
    </div>
  );
}
