import type { Metadata } from "next";
import { AuthHeading } from "@/components/auth/auth-heading";
import { RegisterForm } from "@/features/auth/register-form";
import { t } from "@/lib/i18n";

export const metadata: Metadata = { title: t("auth.register.title") };

export default function RegisterPage() {
  return (
    <>
      <AuthHeading title={t("auth.register.title")} description={t("auth.register.description")} />
      <RegisterForm />
    </>
  );
}
