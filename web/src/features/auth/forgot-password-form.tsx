"use client";

import Link from "next/link";
import { useState } from "react";
import { useForm } from "react-hook-form";
import { Button } from "@/components/ui/button";
import { Field, FieldError, FieldLabel } from "@/components/ui/field";
import { Input } from "@/components/ui/input";
import { lazyResolver } from "@/lib/auth/lazy-resolver";
import type { ForgotPasswordInput } from "@/lib/auth/schemas";
import { toFriendlyMessage } from "@/lib/errors";
import { t } from "@/lib/i18n";
import { forgotPassword } from "./api";
import { FormAlert } from "./form-alert";

/** The api answers 202 for every email (no enumeration), so the confirmation never says whether it exists. */
export function ForgotPasswordForm() {
  const [error, setError] = useState<string | null>(null);
  const [sentTo, setSentTo] = useState<string | null>(null);
  const form = useForm<ForgotPasswordInput>({ resolver: lazyResolver<ForgotPasswordInput>((s) => s.forgotPasswordSchema), defaultValues: { email: "" } });
  const { errors, isSubmitting } = form.formState;

  const onSubmit = form.handleSubmit(async (values) => {
    setError(null);
    try {
      await forgotPassword(values);
      setSentTo(values.email);
    } catch (e) {
      setError(toFriendlyMessage(e));
    }
  });

  if (sentTo) {
    return (
      <div className="flex flex-col gap-4 text-center">
        <p role="status">{t("auth.forgot.sent", "vi", { email: sentTo })}</p>
        <Link href="/login" className="text-sm font-medium text-primary underline-offset-4 hover:underline">
          {t("auth.forgot.back")}
        </Link>
      </div>
    );
  }

  return (
    <form onSubmit={onSubmit} noValidate className="flex flex-col gap-6">
      <FormAlert message={error} />
      <Field data-invalid={!!errors.email}>
        <FieldLabel htmlFor="forgot-email">{t("auth.email")}</FieldLabel>
        <Input id="forgot-email" type="email" autoComplete="email" aria-invalid={!!errors.email} {...form.register("email")} />
        <FieldError errors={[errors.email]} />
      </Field>
      <Button type="submit" size="lg" disabled={isSubmitting}>
        {isSubmitting ? t("auth.forgot.pending") : t("auth.forgot.submit")}
      </Button>
      <Link href="/login" className="text-center text-sm text-primary underline-offset-4 hover:underline">
        {t("auth.forgot.back")}
      </Link>
    </form>
  );
}
