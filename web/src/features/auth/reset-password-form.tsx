"use client";

import { CircleCheck } from "lucide-react";
import Link from "next/link";
import { useState } from "react";
import { useForm, useWatch } from "react-hook-form";
import { PasswordInput } from "@/components/auth/password-input";
import { PasswordStrengthMeter } from "@/components/auth/password-strength-meter";
import { Button } from "@/components/ui/button";
import { Field, FieldError, FieldGroup, FieldLabel } from "@/components/ui/field";
import { lazyResolver } from "@/lib/auth/lazy-resolver";
import type { ResetPasswordInput } from "@/lib/auth/schemas";
import { toFriendlyMessage } from "@/lib/errors";
import { t } from "@/lib/i18n";
import { resetPassword } from "./api";
import { FormAlert } from "./form-alert";

type Props = { email: string | null; token: string | null };

export function ResetPasswordForm({ email, token }: Props) {
  if (!email || !token) {
    return (
      <div className="flex flex-col gap-3 text-center">
        <h2 className="text-xl font-semibold">{t("auth.reset.invalidLink")}</h2>
        <p className="text-muted-foreground">{t("auth.reset.invalidLinkDescription")}</p>
        <Button asChild variant="outline">
          <Link href="/forgot-password">{t("auth.reset.requestNew")}</Link>
        </Button>
      </div>
    );
  }

  return <ResetForm email={email} token={token} />;
}

function ResetForm({ email, token }: { email: string; token: string }) {
  const [error, setError] = useState<string | null>(null);
  const [done, setDone] = useState(false);
  const form = useForm<ResetPasswordInput>({
    resolver: lazyResolver<ResetPasswordInput>((s) => s.resetPasswordSchema),
    defaultValues: { email, token, password: "", confirmPassword: "" },
  });
  const password = useWatch({ control: form.control, name: "password" });
  const { errors, isSubmitting } = form.formState;

  const onSubmit = form.handleSubmit(async (values) => {
    setError(null);
    try {
      await resetPassword({ email: values.email, token: values.token, newPassword: values.password });
      setDone(true);
    } catch (e) {
      setError(toFriendlyMessage(e));
    }
  });

  if (done) {
    return (
      <div className="flex flex-col items-center gap-3 text-center">
        <CircleCheck aria-hidden className="size-10 text-success" />
        <h2 className="text-xl font-semibold">{t("auth.reset.done")}</h2>
        <p className="text-muted-foreground">{t("auth.reset.doneDescription")}</p>
        <Button asChild>
          <Link href="/login">{t("auth.login.submit")}</Link>
        </Button>
      </div>
    );
  }

  return (
    <form onSubmit={onSubmit} noValidate className="flex flex-col gap-6">
      <p className="text-sm text-muted-foreground">{t("auth.reset.description", "vi", { email })}</p>
      <FormAlert message={error} />
      <FieldGroup>
        <Field data-invalid={!!errors.password}>
          <FieldLabel htmlFor="reset-password">{t("auth.newPassword")}</FieldLabel>
          <PasswordInput id="reset-password" autoComplete="new-password" aria-invalid={!!errors.password} {...form.register("password")} />
          <PasswordStrengthMeter password={password} />
          <FieldError errors={[errors.password]} />
        </Field>
        <Field data-invalid={!!errors.confirmPassword}>
          <FieldLabel htmlFor="reset-confirm">{t("auth.confirmPassword")}</FieldLabel>
          <PasswordInput
            id="reset-confirm"
            autoComplete="new-password"
            aria-invalid={!!errors.confirmPassword}
            {...form.register("confirmPassword")}
          />
          <FieldError errors={[errors.confirmPassword]} />
        </Field>
      </FieldGroup>
      <Button type="submit" size="lg" disabled={isSubmitting}>
        {isSubmitting ? t("auth.reset.pending") : t("auth.reset.submit")}
      </Button>
    </form>
  );
}
