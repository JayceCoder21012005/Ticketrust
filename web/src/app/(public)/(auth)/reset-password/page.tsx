import type { Metadata } from "next";
import { Suspense } from "react";
import { AuthFormSkeleton, AuthHeading } from "@/components/auth/auth-heading";
import { ResetPasswordForm } from "@/features/auth/reset-password-form";
import { t } from "@/lib/i18n";

// The link carries a token: keep it out of search engines and out of the Referer of outgoing links.
export const metadata: Metadata = { title: t("auth.reset.title"), robots: { index: false }, referrer: "no-referrer" };

export default function ResetPasswordPage({ searchParams }: PageProps<"/reset-password">) {
  return (
    <>
      <AuthHeading title={t("auth.reset.title")} />
      <Suspense fallback={<AuthFormSkeleton />}>
        <ResetFromQuery searchParams={searchParams} />
      </Suspense>
    </>
  );
}

async function ResetFromQuery({ searchParams }: Pick<PageProps<"/reset-password">, "searchParams">) {
  const { email, token } = await searchParams;
  return <ResetPasswordForm email={typeof email === "string" ? email : null} token={typeof token === "string" ? token : null} />;
}
