"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { LogOut, MailWarning } from "lucide-react";
import { useRouter } from "next/navigation";
import { useEffect, useState, type ChangeEvent } from "react";
import { useForm } from "react-hook-form";
import { useAuth } from "@/components/auth/auth-provider";
import { ErrorState } from "@/components/shared/error-state";
import { Avatar, AvatarFallback, AvatarImage } from "@/components/ui/avatar";
import { Button } from "@/components/ui/button";
import { Field, FieldError, FieldGroup, FieldLabel } from "@/components/ui/field";
import { Input } from "@/components/ui/input";
import { Skeleton } from "@/components/ui/skeleton";
import { FormAlert } from "@/features/auth/form-alert";
import { loginRedirectUrl, updateSessionUser, type SessionUser } from "@/lib/api/session";
import { lazyResolver } from "@/lib/auth/lazy-resolver";
import type { ProfileInput } from "@/lib/auth/schemas";
import { toFriendlyMessage } from "@/lib/errors";
import { t } from "@/lib/i18n";
import { getMe, updateMe, uploadAvatar } from "./api";

const ME = ["me"] as const;
const AVATAR_MAX_BYTES = 2 * 1024 * 1024;
const AVATAR_TYPES = ["image/jpeg", "image/png", "image/webp", "image/avif"];

export function AccountView() {
  const router = useRouter();
  const { status } = useAuth();

  // proxy.ts already redirects when the hint cookie is missing; this covers a hint that outlived its session.
  useEffect(() => {
    if (status === "anonymous") {
      router.replace(loginRedirectUrl("/account"));
    }
  }, [status, router]);

  return status === "authenticated" ? <AccountDetails /> : <AccountSkeleton />;
}

function AccountDetails() {
  const me = useQuery({ queryKey: ME, queryFn: getMe, staleTime: 60_000 });

  if (me.isPending) {
    return <AccountSkeleton />;
  }

  if (me.isError) {
    return <ErrorState onRetry={() => void me.refetch()} />;
  }

  return (
    <div className="flex flex-col gap-8">
      {!me.data.emailConfirmed ? (
        <p className="flex items-start gap-2 rounded-md border border-warning/40 bg-warning/10 px-3 py-2 text-sm">
          <MailWarning aria-hidden className="mt-0.5 size-4 shrink-0" />
          {t("account.emailUnconfirmed")}
        </p>
      ) : null}
      <AvatarSection user={me.data} />
      <ProfileForm key={me.data.id} user={me.data} />
      <SignOutButton />
    </div>
  );
}

function useSaveProfile() {
  const queryClient = useQueryClient();
  return (user: SessionUser) => {
    queryClient.setQueryData(ME, user);
    updateSessionUser(user);
  };
}

function AvatarSection({ user }: { user: SessionUser }) {
  const save = useSaveProfile();
  const [error, setError] = useState<string | null>(null);
  const upload = useMutation({
    mutationFn: async (file: File) =>
      updateMe({ fullName: user.fullName, phoneNumber: user.phoneNumber, avatarUrl: await uploadAvatar(file) }),
    onSuccess: save,
    onError: (e) => setError(toFriendlyMessage(e)),
  });

  const onFile = (event: ChangeEvent<HTMLInputElement>) => {
    const file = event.target.files?.[0];
    event.target.value = "";
    setError(null);
    if (!file) {
      return;
    }

    if (!AVATAR_TYPES.includes(file.type)) {
      setError(t("account.avatarType"));
    } else if (file.size > AVATAR_MAX_BYTES) {
      setError(t("account.avatarTooLarge"));
    } else {
      upload.mutate(file);
    }
  };

  return (
    <section aria-labelledby="avatar-heading" className="flex items-center gap-4">
      <Avatar className="size-16">
        {user.avatarUrl ? <AvatarImage src={user.avatarUrl} alt="" /> : null}
        <AvatarFallback className="text-lg">{user.fullName.charAt(0).toUpperCase()}</AvatarFallback>
      </Avatar>
      <div className="flex flex-col gap-1">
        <h2 id="avatar-heading" className="font-medium">
          {t("account.avatar")}
        </h2>
        <label
          htmlFor="avatar-file"
          className="w-fit cursor-pointer text-sm font-medium text-primary underline-offset-4 hover:underline has-[:focus-visible]:outline-2"
        >
          {upload.isPending ? t("account.uploading") : t("account.changeAvatar")}
        </label>
        <input
          id="avatar-file"
          type="file"
          accept={AVATAR_TYPES.join(",")}
          className="sr-only"
          disabled={upload.isPending}
          onChange={onFile}
        />
        <p className="text-xs text-muted-foreground">{t("account.avatarHint")}</p>
        {error ? (
          <p role="alert" className="text-sm text-danger">
            {error}
          </p>
        ) : null}
      </div>
    </section>
  );
}

function ProfileForm({ user }: { user: SessionUser }) {
  const save = useSaveProfile();
  const [error, setError] = useState<string | null>(null);
  const [saved, setSaved] = useState(false);
  const form = useForm<ProfileInput>({
    resolver: lazyResolver<ProfileInput>((s) => s.profileSchema),
    defaultValues: { fullName: user.fullName, phoneNumber: user.phoneNumber ?? "" },
  });
  const { errors, isSubmitting } = form.formState;

  const onSubmit = form.handleSubmit(async (values) => {
    setError(null);
    setSaved(false);
    try {
      save(await updateMe({ fullName: values.fullName, phoneNumber: values.phoneNumber || null, avatarUrl: user.avatarUrl }));
      setSaved(true);
    } catch (e) {
      setError(toFriendlyMessage(e));
    }
  });

  return (
    <form onSubmit={onSubmit} noValidate className="flex flex-col gap-6">
      <FormAlert message={error} />
      <FieldGroup>
        <Field>
          <span className="text-sm font-medium">{t("auth.email")}</span>
          <p className="text-sm text-muted-foreground">{user.email}</p>
        </Field>
        <Field data-invalid={!!errors.fullName}>
          <FieldLabel htmlFor="profile-name">{t("auth.fullName")}</FieldLabel>
          <Input id="profile-name" autoComplete="name" aria-invalid={!!errors.fullName} {...form.register("fullName")} />
          <FieldError errors={[errors.fullName]} />
        </Field>
        <Field data-invalid={!!errors.phoneNumber}>
          <FieldLabel htmlFor="profile-phone">{t("account.phone")}</FieldLabel>
          <Input id="profile-phone" type="tel" autoComplete="tel" aria-invalid={!!errors.phoneNumber} {...form.register("phoneNumber")} />
          <FieldError errors={[errors.phoneNumber]} />
        </Field>
      </FieldGroup>
      <div className="flex items-center gap-3">
        <Button type="submit" disabled={isSubmitting}>
          {isSubmitting ? t("account.saving") : t("account.save")}
        </Button>
        <p role="status" className="text-sm text-success">
          {saved ? t("account.saved") : null}
        </p>
      </div>
    </form>
  );
}

function SignOutButton() {
  const router = useRouter();
  const { signOut } = useAuth();
  const [pending, setPending] = useState(false);

  const onClick = async () => {
    setPending(true);
    await signOut();
    router.replace("/");
  };

  return (
    <Button variant="outline" className="w-fit" disabled={pending} onClick={() => void onClick()}>
      <LogOut aria-hidden />
      {t("account.logout")}
    </Button>
  );
}

function AccountSkeleton() {
  return (
    <div aria-busy className="flex flex-col gap-8">
      <div className="flex items-center gap-4">
        <Skeleton className="size-16 rounded-full" />
        <Skeleton className="h-10 w-40" />
      </div>
      <div className="flex flex-col gap-4">
        <Skeleton className="h-14 w-full" />
        <Skeleton className="h-14 w-full" />
        <Skeleton className="h-14 w-full" />
      </div>
    </div>
  );
}
