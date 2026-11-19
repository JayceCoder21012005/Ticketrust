"use client";

import { Minus, Plus } from "lucide-react";
import Link from "next/link";
import { useCallback, useEffect, useId, useState, useSyncExternalStore } from "react";
import { Button } from "@/components/ui/button";
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { getSession, refreshAccessToken, subscribe } from "@/lib/api/session";
import { toFriendlyMessage } from "@/lib/errors";
import { t } from "@/lib/i18n.buyer";
import { cn } from "@/lib/utils";
import { getMyWaitlistEntry, joinWaitlist, leaveWaitlist, type WaitlistEntryStatus } from "@/lib/waitlist/api";
import { WaitlistPlaceholder, type WaitlistButtonProps } from "./waitlist-button";
import { loginHref } from "@/features/follow/follow-ui";

const MAX_QUANTITY = 4;

// The panel and the mobile bar both render a control for the same event: they share one request and one state.
type Entry = WaitlistEntryStatus | null;
const entries = new Map<string, { value: Entry | undefined; failed: boolean; listeners: Set<() => void> }>();

function slot(eventId: string) {
  let found = entries.get(eventId);
  if (!found) {
    found = { value: undefined, failed: false, listeners: new Set() };
    entries.set(eventId, found);
  }
  return found;
}

function publish(eventId: string, value: Entry | undefined, failed = false) {
  const s = slot(eventId);
  s.value = value;
  s.failed = failed;
  s.listeners.forEach((listener) => listener());
}

const loading = new Set<string>();
function load(eventId: string) {
  if (loading.has(eventId)) return;
  loading.add(eventId);
  getMyWaitlistEntry(eventId)
    .then((entry) => publish(eventId, entry), () => publish(eventId, undefined, true))
    .finally(() => loading.delete(eventId));
}

/** For tests: forget shared state between cases. */
export function resetWaitlistState() {
  entries.clear();
  loading.clear();
}

function useEntry(eventId: string) {
  const s = slot(eventId);
  const subscribeEntry = useCallback(
    (listener: () => void) => {
      s.listeners.add(listener);
      return () => s.listeners.delete(listener);
    },
    [s],
  );
  const value = useSyncExternalStore(subscribeEntry, () => s.value, () => undefined);
  const failed = useSyncExternalStore(subscribeEntry, () => s.failed, () => false);
  useEffect(() => {
    if (s.value === undefined && !s.failed) load(eventId);
  }, [eventId, s]);
  return { entry: value, failed, set: (entry: Entry) => publish(eventId, entry), reload: () => load(eventId) };
}

/**
 * The control behind `WaitlistButton`. Like the follow toggle, it restores the session on a public page first, and a stale
 * session hint ends in the login link rather than a redirect.
 */
export default function WaitlistControl(props: WaitlistButtonProps) {
  const session = useSyncExternalStore(subscribe, getSession, () => null);
  const [restoreFailed, setRestoreFailed] = useState(false);

  useEffect(() => {
    if (!getSession()) {
      void refreshAccessToken().finally(() => setRestoreFailed(!getSession()));
    }
  }, []);

  if (!session) {
    return restoreFailed ? (
      <Button asChild size={props.compact ? "default" : "lg"} className={props.className}>
        <Link href={loginHref(`/events/${props.slug}`)} prefetch={false}>
          {t("catalog.cta.soldOut")}
        </Link>
      </Button>
    ) : (
      <WaitlistPlaceholder compact={props.compact} className={props.className} />
    );
  }

  return <Control {...props} />;
}

function Control({ eventId, tiers, maxPerUser, compact, className }: WaitlistButtonProps) {
  const { entry, failed, set, reload } = useEntry(eventId);
  const [open, setOpen] = useState(false);
  const [leaving, setLeaving] = useState(false);
  const [message, setMessage] = useState<string | null>(null);
  const size = compact ? "default" : "lg";

  if (failed) {
    return (
      <p role="alert" className="flex flex-wrap items-center gap-x-2 text-xs text-muted-foreground">
        {t("waitlist.loadFailed")}
        <Button type="button" variant="link" size="xs" onClick={reload} className="h-auto p-0 text-xs">
          {t("common.retry")}
        </Button>
      </p>
    );
  }

  if (entry === undefined) {
    return <WaitlistPlaceholder compact={compact} className={className} />;
  }

  if (entry?.status === "Offered" && entry.offer) {
    return (
      <Button asChild size={size} className={className}>
        <Link href={`/waitlist/offers/${entry.offer.offerId}`}>{t("waitlist.offerReady")}</Link>
      </Button>
    );
  }

  const leave = async () => {
    setLeaving(true);
    setMessage(null);
    try {
      await leaveWaitlist(eventId);
      set(null);
    } catch (error) {
      setMessage(toFriendlyMessage(error));
    } finally {
      setLeaving(false);
    }
  };

  if (entry) {
    return (
      <div className={cn("flex flex-col gap-2", className)}>
        <Button type="button" variant="secondary" size={size} disabled className="w-full">
          {entry.position ? t("waitlist.position", "vi", { position: entry.position }) : t("waitlist.waiting")}
        </Button>
        {compact ? null : (
          <>
            <p className="text-xs text-muted-foreground">{t("waitlist.waitingHint")}</p>
            <Button type="button" variant="ghost" size="sm" onClick={leave} disabled={leaving}>
              {t("waitlist.leave")}
            </Button>
            {message ? (
              <p role="alert" className="text-xs text-danger">
                {message}
              </p>
            ) : null}
          </>
        )}
      </div>
    );
  }

  return (
    <>
      <Button type="button" size={size} className={className} onClick={() => setOpen(true)}>
        {t("catalog.cta.soldOut")}
      </Button>
      <JoinDialog
        open={open}
        onOpenChange={setOpen}
        eventId={eventId}
        tiers={tiers}
        maxQuantity={Math.max(1, Math.min(MAX_QUANTITY, maxPerUser))}
        onJoined={(joined) => {
          set(joined);
          setOpen(false);
        }}
      />
    </>
  );
}

type JoinDialogProps = {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  eventId: string;
  tiers: WaitlistButtonProps["tiers"];
  maxQuantity: number;
  onJoined: (entry: WaitlistEntryStatus) => void;
};

/** Tier (or any tier), how many tickets, and how offers work, before joining. */
export function JoinDialog({ open, onOpenChange, eventId, tiers, maxQuantity, onJoined }: JoinDialogProps) {
  const tierField = useId();
  const [tierId, setTierId] = useState("");
  const [quantity, setQuantity] = useState(1);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<string | null>(null);

  const submit = async () => {
    setBusy(true);
    setMessage(null);
    try {
      onJoined(await joinWaitlist(eventId, tierId || null, quantity));
    } catch (error) {
      setMessage(toFriendlyMessage(error));
    } finally {
      setBusy(false);
    }
  };

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>{t("waitlist.dialog.title")}</DialogTitle>
          <DialogDescription>{t("waitlist.dialog.description")}</DialogDescription>
        </DialogHeader>

        <div className="flex flex-col gap-4">
          <div className="flex flex-col gap-1.5">
            <label htmlFor={tierField} className="text-sm font-medium">
              {t("waitlist.dialog.tier")}
            </label>
            <select
              id={tierField}
              value={tierId}
              onChange={(event) => setTierId(event.target.value)}
              className="h-10 rounded-md border bg-background px-3 text-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
            >
              <option value="">{t("waitlist.dialog.anyTier")}</option>
              {tiers.map((tier) => (
                <option key={tier.id} value={tier.id}>
                  {tier.name}
                </option>
              ))}
            </select>
          </div>

          <div className="flex items-center justify-between gap-3">
            <span id={`${tierField}-qty`} className="text-sm font-medium">
              {t("waitlist.dialog.quantity")}
            </span>
            <div role="group" aria-labelledby={`${tierField}-qty`} className="flex items-center gap-2">
              <Button type="button" variant="outline" size="icon" aria-label={t("waitlist.dialog.fewer")} disabled={quantity <= 1} onClick={() => setQuantity((q) => q - 1)}>
                <Minus aria-hidden />
              </Button>
              <output aria-live="polite" className="w-6 text-center font-semibold tabular-nums">
                {quantity}
              </output>
              <Button type="button" variant="outline" size="icon" aria-label={t("waitlist.dialog.more")} disabled={quantity >= maxQuantity} onClick={() => setQuantity((q) => q + 1)}>
                <Plus aria-hidden />
              </Button>
            </div>
          </div>

          <ul className="list-disc space-y-1 pl-5 text-sm text-muted-foreground">
            <li>{t("waitlist.dialog.howFifo")}</li>
            <li>{t("waitlist.dialog.howHold")}</li>
            <li>{t("waitlist.dialog.howMiss")}</li>
          </ul>

          {message ? (
            <p role="alert" className="text-sm text-danger">
              {message}
            </p>
          ) : null}
        </div>

        <DialogFooter>
          <Button type="button" onClick={submit} disabled={busy}>
            {t("waitlist.dialog.submit")}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
