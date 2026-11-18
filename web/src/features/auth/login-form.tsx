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
import { Turnstile } from "@/lib/captcha/turnstile";
import { ApiError, toFriendlyMessage } from "@/lib/errors";
import { t } from "@/lib/i18n";
import { showRateLimitToast } from "@/lib/rate-limit-toast";
import { FormAlert } from "./form-alert";

/**
 * Reads `?returnUrl=` from the address bar when the user submits, not while rendering: that keeps /login a
 * fully static page (no server-side searchParams, no streamed dynamic part) for a faster first paint.
 */
export function LoginForm() {
  const router = useRouter();
  const { signIn } = useAuth();
  const [error, setError] = useState<string | null>(null);
  const [signedIn, setSignedIn] = useState(false);
  // Spec 12: after 3 failed sign-ins the api asks for Turnstile (`captcha.required`); from then on every try sends a fresh token.
  const [captchaNeeded, setCaptchaNeeded] = useState(false);
  const [captcha, setCaptcha] = useState<string | null>(null);
  const [captchaRound, setCaptchaRound] = useState(0);
  const form = useForm<LoginInput>({ resolver: lazyResolver<LoginInput>((s) => s.loginSchema), defaultValues: { email: "", password: "" } });
  const { errors, isSubmitting } = form.formState;
  // Stays disabled after success too: navigation takes a moment and a second submit would log in twice.
  const pending = isSubmitting || signedIn;

  const onSubmit = form.handleSubmit(async (values) => {
    setError(null);
    if (captchaNeeded && !captcha) {
      setError(t("auth.captcha.pending"));
      return;
    }

    try {
      await signIn({ ...values, captchaToken: captchaNeeded ? captcha : null });
      setSignedIn(true);
      router.replace(safeReturnUrl(new URLSearchParams(window.location.search).get("returnUrl")));
    } catch (e) {
      if (captchaNeeded) setCaptchaRound((n) => n + 1);
      if (e instanceof ApiError && (e.code === "captcha.required" || e.code === "captcha.failed")) setCaptchaNeeded(true);
      if (!showRateLimitToast(e)) setError(toFriendlyMessage(e));
    }
  });

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
      {captchaNeeded ? <Turnstile onToken={setCaptcha} resetKey={captchaRound} /> : null}
      <Button type="submit" size="lg" disabled={pending}>
        {pending ? t("auth.login.pending") : t("auth.login.submit")}
      </Button>
      <p className="text-center text-sm text-muted-foreground">
        {t("auth.login.noAccount")}{" "}
        <Link href="/register" className="font-medium text-primary underline-offset-4 hover:underline">
          {t("auth.login.register")}
        </Link>
      </p>
    </form>
  );
}
