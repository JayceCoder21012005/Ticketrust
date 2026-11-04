"use client";

import { KeyRound } from "lucide-react";
import { useId, useState, type FormEvent } from "react";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { t } from "@/lib/i18n";

/** Presale phases ask for a code before the buyer may enter the waiting room. */
export function AccessCodeForm({ onSubmit, error, pending = false }: { onSubmit: (code: string) => void; error?: string; pending?: boolean }) {
  const [code, setCode] = useState("");
  const inputId = useId();
  const errorId = useId();

  const submit = (event: FormEvent) => {
    event.preventDefault();
    const trimmed = code.trim();
    if (trimmed) {
      onSubmit(trimmed);
    }
  };

  return (
    <form onSubmit={submit} className="flex flex-col gap-4" noValidate>
      <div className="flex flex-col items-center gap-2 text-center">
        <KeyRound aria-hidden className="size-9 text-primary" />
        <h2 className="text-xl font-semibold">{t("queue.accessCode.title")}</h2>
        <p className="text-sm text-muted-foreground">{t("queue.accessCode.description")}</p>
      </div>
      <div className="flex flex-col gap-1.5">
        <Label htmlFor={inputId}>{t("queue.accessCode.label")}</Label>
        <Input
          id={inputId}
          value={code}
          onChange={(event) => setCode(event.target.value)}
          autoComplete="off"
          autoCapitalize="characters"
          aria-invalid={error ? true : undefined}
          aria-describedby={error ? errorId : undefined}
        />
        {error ? (
          <p id={errorId} role="alert" className="text-sm text-destructive">
            {error}
          </p>
        ) : null}
      </div>
      <Button type="submit" disabled={pending}>
        {t("queue.accessCode.submit")}
      </Button>
    </form>
  );
}
