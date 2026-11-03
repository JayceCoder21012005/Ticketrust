import type { Metadata } from "next";
import { AuthHeading } from "@/components/auth/auth-heading";
import { ForgotPasswordForm } from "@/features/auth/forgot-password-form";
import { t } from "@/lib/i18n";

export const metadata: Metadata = { title: t("auth.forgot.title") };

export default function ForgotPasswordPage() {
  return (
    <>
      <AuthHeading title={t("auth.forgot.title")} description={t("auth.forgot.description")} />
      <ForgotPasswordForm />
    </>
  );
}
