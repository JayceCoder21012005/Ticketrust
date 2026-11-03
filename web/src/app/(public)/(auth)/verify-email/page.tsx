import type { Metadata } from "next";
import { Suspense } from "react";
import { AuthFormSkeleton, AuthHeading } from "@/components/auth/auth-heading";
import { VerifyEmail } from "@/features/auth/verify-email";
import { t } from "@/lib/i18n";

// The link carries a token: keep it out of search engines and out of the Referer of outgoing links.
export const metadata: Metadata = { title: t("auth.verify.title"), robots: { index: false }, referrer: "no-referrer" };

export default function VerifyEmailPage({ searchParams }: PageProps<"/verify-email">) {
  return (
    <>
      <AuthHeading title={t("auth.verify.title")} />
      <Suspense fallback={<AuthFormSkeleton />}>
        <VerifyFromQuery searchParams={searchParams} />
      </Suspense>
    </>
  );
}

async function VerifyFromQuery({ searchParams }: Pick<PageProps<"/verify-email">, "searchParams">) {
  const { userId, token } = await searchParams;
  return <VerifyEmail userId={typeof userId === "string" ? userId : null} token={typeof token === "string" ? token : null} />;
}
