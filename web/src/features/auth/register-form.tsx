"use client";

import { MailCheck } from "lucide-react";
import Link from "next/link";
import { useState } from "react";
import { useForm, useWatch } from "react-hook-form";
import { PasswordInput } from "@/components/auth/password-input";
import { PasswordStrengthMeter } from "@/components/auth/password-strength-meter";
import { Button } from "@/components/ui/button";
import { Field, FieldError, FieldGroup, FieldLabel } from "@/components/ui/field";
import { Input } from "@/components/ui/input";
import { lazyResolver } from "@/lib/auth/lazy-resolver";
import type { RegisterInput } from "@/lib/auth/schemas";
import { Turnstile } from "@/lib/captcha/turnstile";
import { toFriendlyMessage } from "@/lib/errors";
import { t } from "@/lib/i18n";
import { showRateLimitToast } from "@/lib/rate-limit-toast";
import { register as registerAccount } from "./api";
import { FormAlert } from "./form-alert";

export function RegisterForm() {
  const [error, setError] = useState<string | null>(null);
  const [sentTo, setSentTo] = useState<string | null>(null);
  const [captcha, setCaptcha] = useState<string | null>(null);
  // A Turnstile token is single-use: every submit that reached the api asks for a fresh one.
  const [captchaRound, setCaptchaRound] = useState(0);
  const [website, setWebsite] = useState("");
  const form = useForm<RegisterInput>({
    resolver: lazyResolver<RegisterInput>((s) => s.registerSchema),
    defaultValues: { fullName: "", email: "", password: "" },
  });
  const password = useWatch({ control: form.control, name: "password" });
  const { errors, isSubmitting } = form.formState;

  const onSubmit = form.handleSubmit(async (values) => {
    setError(null);
    if (!captcha) {
      setError(t("auth.captcha.pending"));
      return;
    }

    try {
      await registerAccount({ ...values, captchaToken: captcha, website });
      setSentTo(values.email);
    } catch (e) {
      setCaptchaRound((n) => n + 1);
      if (!showRateLimitToast(e)) setError(toFriendlyMessage(e));
    }
  });

  if (sentTo) {
    return (
      <div className="flex flex-col items-center gap-3 text-center">
        <MailCheck aria-hidden className="size-10 text-primary" />
        <h2 className="text-xl font-semibold">{t("auth.register.checkInbox")}</h2>
        <p className="text-muted-foreground">{t("auth.register.sent", "vi", { email: sentTo })}</p>
        <Button asChild variant="outline">
          <Link href="/login">{t("auth.login.submit")}</Link>
        </Button>
      </div>
    );
  }

  return (
    <form onSubmit={onSubmit} noValidate className="flex flex-col gap-6">
      <FormAlert message={error} />
      <FieldGroup>
        <Field data-invalid={!!errors.fullName}>
          <FieldLabel htmlFor="register-name">{t("auth.fullName")}</FieldLabel>
          <Input id="register-name" autoComplete="name" aria-invalid={!!errors.fullName} {...form.register("fullName")} />
          <FieldError errors={[errors.fullName]} />
        </Field>
        <Field data-invalid={!!errors.email}>
          <FieldLabel htmlFor="register-email">{t("auth.email")}</FieldLabel>
          <Input id="register-email" type="email" autoComplete="email" aria-invalid={!!errors.email} {...form.register("email")} />
          <FieldError errors={[errors.email]} />
        </Field>
        <Field data-invalid={!!errors.password}>
          <FieldLabel htmlFor="register-password">{t("auth.password")}</FieldLabel>
          <PasswordInput
            id="register-password"
            autoComplete="new-password"
            aria-invalid={!!errors.password}
            {...form.register("password")}
          />
          <PasswordStrengthMeter password={password} />
          <FieldError errors={[errors.password]} />
        </Field>
      </FieldGroup>
      {/* Honeypot (spec 12): off-screen and out of the tab order; people never fill it, form-filling bots do. */}
      <div aria-hidden className="absolute -left-[9999px] h-px w-px overflow-hidden">
        <label htmlFor="register-website">{t("auth.captcha.honeypot")}</label>
        <input
          id="register-website"
          name="website"
          type="text"
          tabIndex={-1}
          autoComplete="off"
          value={website}
          onChange={(e) => setWebsite(e.target.value)}
        />
      </div>
      <Turnstile onToken={setCaptcha} resetKey={captchaRound} />
      <Button type="submit" size="lg" disabled={isSubmitting}>
        {isSubmitting ? t("auth.register.pending") : t("auth.register.submit")}
      </Button>
      <p className="text-center text-sm text-muted-foreground">
        {t("auth.register.hasAccount")}{" "}
        <Link href="/login" className="font-medium text-primary underline-offset-4 hover:underline">
          {t("auth.login.submit")}
        </Link>
      </p>
    </form>
  );
}
