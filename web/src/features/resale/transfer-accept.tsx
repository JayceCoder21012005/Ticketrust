"use client";

import Link from "next/link";
import { useCallback, useEffect, useState } from "react";
import { Button } from "@/components/ui/button";
import { Skeleton } from "@/components/ui/skeleton";
import { ApiError, toFriendlyMessage } from "@/lib/errors";
import { formatDateTime } from "@/lib/format";
import { t } from "@/lib/i18n.buyer";
import { acceptTransfer, getTransfer, type TransferInvite } from "@/lib/resale/api";

type State = { kind: "loading" } | { kind: "ready"; invite: TransferInvite } | { kind: "notFound" } | { kind: "failed" };

/** `/transfers/[id]`: what the friend sends, and "Nhận vé", which makes it the caller's ticket with a new QR. */
export function TransferAcceptView({ transferId }: { transferId: string }) {
  const [state, setState] = useState<State>({ kind: "loading" });
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<string | null>(null);

  const load = useCallback(() => {
    const controller = new AbortController();
    getTransfer(transferId, controller.signal).then(
      (invite) => setState({ kind: "ready", invite }),
      (error: unknown) => {
        if (controller.signal.aborted) return;
        setState(error instanceof ApiError && error.status === 404 ? { kind: "notFound" } : { kind: "failed" });
      },
    );
    return () => controller.abort();
  }, [transferId]);

  useEffect(() => load(), [load]);

  if (state.kind === "loading") return <Skeleton className="h-80 w-full rounded-2xl" />;

  if (state.kind !== "ready") {
    return (
      <div className="flex flex-col items-start gap-3 rounded-2xl border bg-card p-6">
        <h1 className="text-xl font-bold">{t(state.kind === "notFound" ? "resale.accept.notFound" : "resale.accept.loadFailed")}</h1>
        {state.kind === "notFound" ? <p className="text-sm text-muted-foreground">{t("resale.accept.notFoundHint")}</p> : null}
        {state.kind === "failed" ? (
          <Button type="button" variant="outline" onClick={() => { setState({ kind: "loading" }); load(); }}>
            {t("common.retry")}
          </Button>
        ) : null}
      </div>
    );
  }

  const { invite } = state;
  const accept = async () => {
    setBusy(true);
    setMessage(null);
    try {
      setState({ kind: "ready", invite: await acceptTransfer(transferId) });
    } catch (error) {
      setMessage(toFriendlyMessage(error));
    } finally {
      setBusy(false);
    }
  };

  const accepted = invite.status === "Accepted" && invite.newTicketId;
  const open = invite.status === "Pending";

  return (
    <article className="flex flex-col gap-5 rounded-2xl border bg-card p-6 shadow-sm">
      <header className="flex flex-col gap-1">
        <h1 className="text-brand-gradient text-2xl font-bold tracking-tight">
          {accepted ? t("resale.accept.accepted") : open ? t("resale.accept.title") : t("resale.accept.closed")}
        </h1>
        <p className="text-sm text-muted-foreground">
          {open || accepted ? t("resale.accept.from", "vi", { name: invite.fromName }) : t("resale.accept.closedHint")}
        </p>
      </header>
      <div className="flex flex-col gap-1">
        <Link href={`/events/${invite.eventSlug}`} className="font-semibold hover:underline">
          {invite.eventTitle}
        </Link>
        <p className="text-sm text-muted-foreground">{`${formatDateTime(invite.startsAt)} · ${invite.venue}`}</p>
        <p className="text-sm">
          {invite.tierName}
          {invite.seatLabel ? ` · ${t("resale.accept.seat", "vi", { seat: invite.seatLabel })}` : null}
        </p>
      </div>
      {open ? <p className="text-sm text-muted-foreground">{t("resale.accept.expires", "vi", { expires: formatDateTime(invite.expiresAt) })}</p> : null}
      {message ? (
        <p role="alert" className="text-sm text-danger">
          {message}
        </p>
      ) : null}
      {accepted ? (
        <Button asChild size="lg">
          <Link href={`/tickets/${invite.newTicketId}`}>{t("resale.accept.openTicket")}</Link>
        </Button>
      ) : open ? (
        <Button type="button" size="lg" onClick={accept} disabled={busy}>
          {t("resale.accept.submit")}
        </Button>
      ) : null}
    </article>
  );
}
