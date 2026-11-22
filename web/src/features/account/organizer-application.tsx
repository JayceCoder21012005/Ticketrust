"use client";

import Link from "next/link";
import { useEffect, useId, useState, type FormEvent } from "react";
import { useAuth } from "@/components/auth/auth-provider";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Skeleton } from "@/components/ui/skeleton";
import { Textarea } from "@/components/ui/textarea";
import { api } from "@/lib/api/client";
import type { components } from "@/lib/api/schema";
import { ApiError, toFriendlyMessage, type ProblemDetails } from "@/lib/errors";
import { t } from "@/lib/i18n.buyer";

type Application = components["schemas"]["OrganizerApplicationView"];

async function getMine(): Promise<Application | null> {
  const result = await api.GET("/api/v1/me/organizer-application", { cache: "no-store" });
  if (result.response.status === 404) return null;
  if (result.data === undefined) throw new ApiError(result.response.status, (result.error ?? {}) as ProblemDetails);
  return result.data;
}

async function submit(body: { companyName: string; phone: string; description: string }): Promise<Application> {
  const result = await api.POST("/api/v1/organizer-applications", { body });
  if (result.data === undefined) throw new ApiError(result.response.status, (result.error ?? {}) as ProblemDetails);
  return result.data;
}

/** `/account/organizer` (spec 16): apply to sell tickets, then follow the admin's decision. */
export function OrganizerApplicationView() {
  const id = useId();
  const { user } = useAuth();
  const [application, setApplication] = useState<Application | null | undefined>(undefined);
  const [form, setForm] = useState({ companyName: "", phone: "", description: "" });
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<string | null>(null);

  useEffect(() => {
    getMine().then(setApplication, () => setApplication(null));
  }, []);

  if (user?.roles.includes("Organizer")) {
    return (
      <section className="flex flex-col items-start gap-3">
        <h1 className="text-2xl font-bold">{t("accountHub.organizer.title")}</h1>
        <p className="text-sm text-muted-foreground">{t("accountHub.organizer.already")}</p>
        <Button asChild>
          <Link href="/organizer">{t("accountHub.organizer.open")}</Link>
        </Button>
      </section>
    );
  }

  if (application === undefined) return <Skeleton className="h-64 w-full" />;

  const onSubmit = async (event: FormEvent) => {
    event.preventDefault();
    setBusy(true);
    setMessage(null);
    try {
      setApplication(await submit({ ...form, companyName: form.companyName.trim(), phone: form.phone.trim() }));
    } catch (error) {
      setMessage(toFriendlyMessage(error));
    } finally {
      setBusy(false);
    }
  };

  const pending = application?.status === "Pending";
  return (
    <section className="flex flex-col gap-5">
      <header className="flex flex-col gap-1">
        <h1 className="text-2xl font-bold">{t("accountHub.organizer.title")}</h1>
        <p className="text-sm text-muted-foreground">{t("accountHub.organizer.intro")}</p>
      </header>
      {application ? (
        <div className="flex flex-col gap-2 rounded-xl border p-4" role="status">
          <p className="flex items-center gap-2 font-medium">
            {application.companyName}
            <Badge variant={application.status === "Rejected" ? "destructive" : "secondary"}>{t(`accountHub.organizer.status.${application.status}`)}</Badge>
          </p>
          {application.decisionReason ? <p className="text-sm text-muted-foreground">{t("accountHub.organizer.reason", "vi", { reason: application.decisionReason })}</p> : null}
        </div>
      ) : null}
      {pending ? null : (
        <form onSubmit={onSubmit} className="flex max-w-xl flex-col gap-4">
          <div className="flex flex-col gap-1.5">
            <label htmlFor={`${id}-company`} className="text-sm font-medium">
              {t("accountHub.organizer.company")}
            </label>
            <Input id={`${id}-company`} required maxLength={200} value={form.companyName} onChange={(e) => setForm({ ...form, companyName: e.target.value })} />
          </div>
          <div className="flex flex-col gap-1.5">
            <label htmlFor={`${id}-phone`} className="text-sm font-medium">
              {t("accountHub.organizer.phone")}
            </label>
            <Input id={`${id}-phone`} required type="tel" maxLength={32} value={form.phone} onChange={(e) => setForm({ ...form, phone: e.target.value })} />
          </div>
          <div className="flex flex-col gap-1.5">
            <label htmlFor={`${id}-description`} className="text-sm font-medium">
              {t("accountHub.organizer.description")}
            </label>
            <Textarea id={`${id}-description`} maxLength={2000} value={form.description} onChange={(e) => setForm({ ...form, description: e.target.value })} />
          </div>
          {message ? (
            <p role="alert" className="text-sm text-danger">
              {message}
            </p>
          ) : null}
          <Button type="submit" disabled={busy || !form.companyName.trim() || !form.phone.trim()} className="self-start">
            {t("accountHub.organizer.submit")}
          </Button>
        </form>
      )}
    </section>
  );
}
