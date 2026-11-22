"use client";

import { useId, useState } from "react";
import { Button } from "@/components/ui/button";
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { Input } from "@/components/ui/input";
import { Textarea } from "@/components/ui/textarea";
import { toFriendlyMessage } from "@/lib/errors";

export type ReasonDialogProps = {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  title: string;
  description: string;
  confirmLabel: string;
  /** Destructive actions ask to type this (the target's name or id) before the button enables. */
  confirmText?: string;
  destructive?: boolean;
  onConfirm: (reason: string, confirm: string) => Promise<void>;
};

/**
 * Every admin change asks for a reason (it goes into the audit entry, spec 16). A destructive one also asks for the target's name
 * typed back, as a second confirmation, and is announced as an alert dialog.
 */
export function ReasonDialog({ open, onOpenChange, title, description, confirmLabel, confirmText, destructive, onConfirm }: ReasonDialogProps) {
  const id = useId();
  const [reason, setReason] = useState("");
  const [typed, setTyped] = useState("");
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<string | null>(null);
  const ready = reason.trim().length > 0 && reason.trim().length <= 500 && (!confirmText || typed.trim() === confirmText);

  const close = (next: boolean) => {
    if (!next) {
      setReason("");
      setTyped("");
      setMessage(null);
    }
    onOpenChange(next);
  };

  const submit = async () => {
    setBusy(true);
    setMessage(null);
    try {
      await onConfirm(reason.trim(), typed.trim());
      close(false);
    } catch (error) {
      setMessage(toFriendlyMessage(error));
    } finally {
      setBusy(false);
    }
  };

  return (
    <Dialog open={open} onOpenChange={close}>
      <DialogContent role={destructive ? "alertdialog" : "dialog"}>
        <DialogHeader>
          <DialogTitle>{title}</DialogTitle>
          <DialogDescription>{description}</DialogDescription>
        </DialogHeader>
        <div className="flex flex-col gap-3">
          <div className="flex flex-col gap-1.5">
            <label htmlFor={`${id}-reason`} className="text-sm font-medium">
              Lý do (ghi vào nhật ký)
            </label>
            <Textarea id={`${id}-reason`} value={reason} maxLength={500} onChange={(event) => setReason(event.target.value)} />
          </div>
          {confirmText ? (
            <div className="flex flex-col gap-1.5">
              <label htmlFor={`${id}-confirm`} className="text-sm font-medium">
                Nhập <span className="font-mono">{confirmText}</span> để xác nhận
              </label>
              <Input id={`${id}-confirm`} value={typed} autoComplete="off" onChange={(event) => setTyped(event.target.value)} />
            </div>
          ) : null}
          {message ? (
            <p role="alert" className="text-sm text-danger">
              {message}
            </p>
          ) : null}
        </div>
        <DialogFooter>
          <Button type="button" variant="outline" onClick={() => close(false)}>
            Huỷ
          </Button>
          <Button type="button" variant={destructive ? "destructive" : "default"} onClick={submit} disabled={!ready || busy}>
            {confirmLabel}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
