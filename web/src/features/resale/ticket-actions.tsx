"use client";

import { MoreHorizontal, Send, Tag, TriangleAlert, Undo2 } from "lucide-react";
import { useId, useState } from "react";
import { Button } from "@/components/ui/button";
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuTrigger } from "@/components/ui/dropdown-menu";
import { Input } from "@/components/ui/input";
import { Sheet, SheetContent, SheetDescription, SheetFooter, SheetHeader, SheetTitle } from "@/components/ui/sheet";
import { toFriendlyMessage } from "@/lib/errors";
import { formatDateTime, formatVnd } from "@/lib/format";
import { t } from "@/lib/i18n.buyer";
import { cancelListing, cancelTransfer, createListing, createTransfer } from "@/lib/resale/api";
import type { TicketDetail } from "@/lib/tickets/types";

type Open = "resell" | "transfer" | null;

/**
 * The wallet's "…" menu (spec 14): resell or send a ticket that can still change hands, or take back a live listing or
 * transfer. Every change reloads the ticket through `onChanged`.
 */
export function TicketActions({ ticket, onChanged }: { ticket: TicketDetail; onChanged: () => void }) {
  const [open, setOpen] = useState<Open>(null);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<string | null>(null);
  const { resale } = ticket;
  const face = Number(resale.faceValue);
  const listing = resale.listing;
  const transfer = resale.transfer;

  if (!resale.canChange && !listing && !transfer) return null;

  const undo = async (action: () => Promise<void>) => {
    setBusy(true);
    setMessage(null);
    try {
      await action();
      onChanged();
    } catch (error) {
      setMessage(toFriendlyMessage(error));
    } finally {
      setBusy(false);
    }
  };

  return (
    <section className="flex flex-col gap-2 rounded-xl border px-4 py-3">
      <div className="flex items-center justify-between gap-3">
        <p className="min-w-0 text-sm">
          {listing
            ? listing.status === "Reserved"
              ? t("resale.listedReserved")
              : t("resale.listed", "vi", { price: formatVnd(Number(listing.price)) })
            : transfer
              ? t("resale.transferring", "vi", { email: transfer.toEmail, expires: formatDateTime(transfer.expiresAt) })
              : null}
        </p>
        <DropdownMenu>
          <DropdownMenuTrigger asChild>
            <Button type="button" variant="ghost" size="icon" aria-label={t("resale.menu")} disabled={busy}>
              <MoreHorizontal aria-hidden />
            </Button>
          </DropdownMenuTrigger>
          <DropdownMenuContent align="end">
            {resale.canChange ? (
              <>
                <DropdownMenuItem onSelect={() => setOpen("resell")}>
                  <Tag aria-hidden />
                  {t("resale.resell")}
                </DropdownMenuItem>
                <DropdownMenuItem onSelect={() => setOpen("transfer")}>
                  <Send aria-hidden />
                  {t("resale.transfer")}
                </DropdownMenuItem>
              </>
            ) : null}
            {listing?.status === "Active" ? (
              <DropdownMenuItem onSelect={() => void undo(() => cancelListing(listing.listingId))}>
                <Undo2 aria-hidden />
                {t("resale.cancelListing")}
              </DropdownMenuItem>
            ) : null}
            {transfer ? (
              <DropdownMenuItem onSelect={() => void undo(() => cancelTransfer(transfer.transferId))}>
                <Undo2 aria-hidden />
                {t("resale.cancelTransfer")}
              </DropdownMenuItem>
            ) : null}
          </DropdownMenuContent>
        </DropdownMenu>
      </div>
      {message ? (
        <p role="alert" className="text-sm text-danger">
          {message}
        </p>
      ) : null}
      <ResaleSheet
        open={open === "resell"}
        onOpenChange={(next) => setOpen(next ? "resell" : null)}
        ticketId={ticket.id}
        faceValue={face}
        onDone={() => {
          setOpen(null);
          onChanged();
        }}
      />
      <TransferDialog
        open={open === "transfer"}
        onOpenChange={(next) => setOpen(next ? "transfer" : null)}
        ticketId={ticket.id}
        onDone={() => {
          setOpen(null);
          onChanged();
        }}
      />
    </section>
  );
}

type FormProps = { open: boolean; onOpenChange: (open: boolean) => void; ticketId: string; onDone: () => void };

/** Price input and a slider, both capped at face value; the seller gets the whole price (no fee). */
export function ResaleSheet({ open, onOpenChange, ticketId, faceValue, onDone }: FormProps & { faceValue: number }) {
  const id = useId();
  const [price, setPrice] = useState(faceValue);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<string | null>(null);
  const valid = Number.isInteger(price) && price >= 1 && price <= faceValue;

  const submit = async () => {
    setBusy(true);
    setMessage(null);
    try {
      await createListing(ticketId, price);
      onDone();
    } catch (error) {
      setMessage(toFriendlyMessage(error));
    } finally {
      setBusy(false);
    }
  };

  return (
    <Sheet open={open} onOpenChange={onOpenChange}>
      <SheetContent side="bottom" className="mx-auto max-w-md rounded-t-2xl">
        <SheetHeader>
          <SheetTitle>{t("resale.sheet.title")}</SheetTitle>
          <SheetDescription>{t("resale.sheet.description")}</SheetDescription>
        </SheetHeader>
        <div className="flex flex-col gap-4 px-4">
          <div className="flex flex-col gap-1.5">
            <label htmlFor={`${id}-price`} className="text-sm font-medium">
              {t("resale.sheet.price")}
            </label>
            <Input
              id={`${id}-price`}
              type="number"
              inputMode="numeric"
              min={1}
              max={faceValue}
              step={1000}
              value={Number.isNaN(price) ? "" : price}
              aria-describedby={`${id}-hint`}
              aria-invalid={!valid}
              onChange={(event) => setPrice(Math.floor(Number(event.target.value)))}
            />
            <p id={`${id}-hint`} className="text-xs text-muted-foreground">
              {t("resale.sheet.priceHint", "vi", { max: formatVnd(faceValue) })}
            </p>
            <input
              type="range"
              aria-label={t("resale.sheet.price")}
              min={1}
              max={faceValue}
              step={Math.max(1, Math.round(faceValue / 100))}
              value={valid ? price : faceValue}
              onChange={(event) => setPrice(Number(event.target.value))}
              className="w-full accent-primary"
            />
          </div>
          <p className="text-lg font-semibold" aria-live="polite">
            {t("resale.sheet.youGet", "vi", { amount: formatVnd(valid ? price : 0) })}
          </p>
          <ul className="list-disc space-y-1 pl-5 text-sm text-muted-foreground">
            <li>{t("resale.sheet.ruleWaitlist")}</li>
            <li>{t("resale.sheet.rulePayout")}</li>
            <li>{t("resale.sheet.ruleCancel")}</li>
          </ul>
          {message ? (
            <p role="alert" className="text-sm text-danger">
              {message}
            </p>
          ) : null}
        </div>
        <SheetFooter>
          <Button type="button" onClick={submit} disabled={busy || !valid}>
            {t("resale.sheet.submit")}
          </Button>
        </SheetFooter>
      </SheetContent>
    </Sheet>
  );
}

/** Recipient email and the warning that the current QR dies once they accept. */
export function TransferDialog({ open, onOpenChange, ticketId, onDone }: FormProps) {
  const id = useId();
  const [email, setEmail] = useState("");
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<string | null>(null);

  const submit = async () => {
    setBusy(true);
    setMessage(null);
    try {
      await createTransfer(ticketId, email.trim());
      onDone();
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
          <DialogTitle>{t("resale.dialog.title")}</DialogTitle>
          <DialogDescription>{t("resale.dialog.description")}</DialogDescription>
        </DialogHeader>
        <div className="flex flex-col gap-4">
          <div className="flex flex-col gap-1.5">
            <label htmlFor={`${id}-email`} className="text-sm font-medium">
              {t("resale.dialog.email")}
            </label>
            <Input id={`${id}-email`} type="email" autoComplete="email" value={email} onChange={(event) => setEmail(event.target.value)} />
          </div>
          <p className="flex items-start gap-2 rounded-lg bg-warning/10 px-3 py-2 text-sm">
            <TriangleAlert aria-hidden className="mt-0.5 size-4 shrink-0 text-warning" />
            <span>{t("resale.dialog.warning")}</span>
          </p>
          {message ? (
            <p role="alert" className="text-sm text-danger">
              {message}
            </p>
          ) : null}
        </div>
        <DialogFooter>
          <Button type="button" onClick={submit} disabled={busy || !email.includes("@")}>
            {t("resale.dialog.submit")}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
