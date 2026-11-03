import type { Metadata } from "next";
import { AuthHeading } from "@/components/auth/auth-heading";
import { LoginForm } from "@/features/auth/login-form";
import { t } from "@/lib/i18n";

export const metadata: Metadata = { title: t("auth.login.title") };

export default function LoginPage() {
  return (
    <>
      <AuthHeading title={t("auth.login.title")} description={t("auth.login.description")} />
      <LoginForm />
    </>
  );
}
