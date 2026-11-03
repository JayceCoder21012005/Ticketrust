"use client";

import Link from "next/link";
import { useRouter } from "next/navigation";
import { useState } from "react";
import { useForm } from "react-hook-form";
import { useAuth } from "@/components/auth/auth-provider";
import { PasswordInput } from "@/components/auth/password-input";
import { Button } from "@/components/ui/button";
import { Field, FieldError, FieldGroup, FieldLabel } from "@/components/ui/field";
import { Input } from "@/components/ui/input";
import { safeReturnUrl } from "@/lib/auth/return-url";
import { lazyResolver } from "@/lib/auth/lazy-resolver";
import type { LoginInput } from "@/lib/auth/schemas";
import { toFriendlyMessage } from "@/lib/errors";
import { t } from "@/lib/i18n";
import { FormAlert } from "./form-alert";

export function LoginForm({ returnUrl }: { returnUrl: string | null }) {
  const router = useRouter();
  const { signIn } = useAuth();
  const [error, setError] = useState<string | null>(null);
  const [signedIn, setSignedIn] = useState(false);
  const form = useForm<LoginInput>({ resolver: lazyResolver<LoginInput>((s) => s.loginSchema), defaultValues: { email: "", password: "" } });
  const { errors, isSubmitting } = form.formState;
  // Stays disabled after success too: navigation takes a moment and a second submit would log in twice.
  const pending = isSubmitting || signedIn;

  const onSubmit = form.handleSubmit(async (values) => {
    setError(null);
    try {
      await signIn(values);
      setSignedIn(true);
      router.replace(safeReturnUrl(returnUrl));
    } catch (e) {
      setError(toFriendlyMessage(e));
    }
  });

  const registerHref = returnUrl ? `/register?returnUrl=${encodeURIComponent(returnUrl)}` : "/register";

  return (
    <form onSubmit={onSubmit} noValidate className="flex flex-col gap-6">
      <FormAlert message={error} />
      <FieldGroup>
        <Field data-invalid={!!errors.email}>
          <FieldLabel htmlFor="login-email">{t("auth.email")}</FieldLabel>
          <Input id="login-email" type="email" autoComplete="email" aria-invalid={!!errors.email} {...form.register("email")} />
          <FieldError errors={[errors.email]} />
        </Field>
        <Field data-invalid={!!errors.password}>
          <div className="flex items-center justify-between">
            <FieldLabel htmlFor="login-password">{t("auth.password")}</FieldLabel>
            <Link href="/forgot-password" className="text-sm text-primary underline-offset-4 hover:underline">
              {t("auth.login.forgot")}
            </Link>
          </div>
          <PasswordInput
            id="login-password"
            autoComplete="current-password"
            aria-invalid={!!errors.password}
            {...form.register("password")}
          />
          <FieldError errors={[errors.password]} />
        </Field>
      </FieldGroup>
      <Button type="submit" size="lg" disabled={pending}>
        {pending ? t("auth.login.pending") : t("auth.login.submit")}
      </Button>
      <p className="text-center text-sm text-muted-foreground">
        {t("auth.login.noAccount")}{" "}
        <Link href={registerHref} className="font-medium text-primary underline-offset-4 hover:underline">
          {t("auth.login.register")}
        </Link>
      </p>
    </form>
  );
}
