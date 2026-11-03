"use client";

import { CircleCheck, CircleX, LoaderCircle } from "lucide-react";
import Link from "next/link";
import { useEffect, useRef, useState } from "react";
import { Button } from "@/components/ui/button";
import { t } from "@/lib/i18n";
import { confirmEmail } from "./api";

type State = "pending" | "done" | "failed";

/** Confirms on arrival: the link itself is the user's intent, and confirming twice would be harmless anyway. */
export function VerifyEmail({ userId, token }: { userId: string | null; token: string | null }) {
  const complete = Boolean(userId && token);
  const [state, setState] = useState<State>(complete ? "pending" : "failed");
  // StrictMode mounts effects twice in dev; one confirmation request is enough.
  const started = useRef(false);

  useEffect(() => {
    if (!userId || !token || started.current) {
      return;
    }

    started.current = true;
    confirmEmail({ userId, token }).then(
      () => setState("done"),
      () => setState("failed"),
    );
  }, [userId, token]);

  if (state === "pending") {
    return (
      <p role="status" className="flex items-center justify-center gap-2 text-muted-foreground">
        <LoaderCircle aria-hidden className="size-5 animate-spin motion-reduce:animate-none" />
        {t("auth.verify.pending")}
      </p>
    );
  }

  const done = state === "done";
  const Icon = done ? CircleCheck : CircleX;
  return (
    <div className="flex flex-col items-center gap-3 text-center">
      <Icon aria-hidden className={done ? "size-10 text-success" : "size-10 text-danger"} />
      <h2 className="text-xl font-semibold">{t(done ? "auth.verify.done" : "auth.verify.failed")}</h2>
      <p className="text-muted-foreground">{t(done ? "auth.verify.doneDescription" : "auth.verify.failedDescription")}</p>
      <Button asChild>
        <Link href={done ? "/" : "/login"}>{t(done ? "auth.verify.continue" : "auth.login.submit")}</Link>
      </Button>
    </div>
  );
}
