import type { Metadata } from "next";
import { Suspense } from "react";
import { AuthFormSkeleton, AuthHeading } from "@/components/auth/auth-heading";
import { LoginForm } from "@/features/auth/login-form";
import { t } from "@/lib/i18n";

export const metadata: Metadata = { title: t("auth.login.title") };

export default function LoginPage({ searchParams }: PageProps<"/login">) {
  return (
    <>
      <AuthHeading title={t("auth.login.title")} description={t("auth.login.description")} />
      <Suspense fallback={<AuthFormSkeleton />}>
        <LoginFormFromQuery searchParams={searchParams} />
      </Suspense>
    </>
  );
}

async function LoginFormFromQuery({ searchParams }: Pick<PageProps<"/login">, "searchParams">) {
  const { returnUrl } = await searchParams;
  return <LoginForm returnUrl={typeof returnUrl === "string" ? returnUrl : null} />;
}
