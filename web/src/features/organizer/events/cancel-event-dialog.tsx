"use client";

import { useMutation } from "@tanstack/react-query";
import { Ban } from "lucide-react";
import { useState } from "react";
import { Button } from "@/components/ui/button";
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle, DialogTrigger } from "@/components/ui/dialog";
import { Field, FieldLabel } from "@/components/ui/field";
import { Input } from "@/components/ui/input";
import { FormAlert } from "@/features/auth/form-alert";
import { toFriendlyMessage } from "@/lib/errors";
import { cancelEvent } from "../api";
import type { OrganizerEvent } from "../types";

/** Spec 05: the organizer types the event title to confirm, so a mis-click can't cancel a live sale. */
export function CancelEventDialog({ event, onCancelled }: { event: OrganizerEvent; onCancelled: (event: OrganizerEvent) => void }) {
  const [open, setOpen] = useState(false);
  const [typed, setTyped] = useState("");
  const cancel = useMutation({
    mutationFn: () => cancelEvent(event.id),
    onSuccess: (cancelled) => {
      setOpen(false);
      onCancelled(cancelled);
    },
  });
  const matches = typed.trim() === event.title.trim();

  return (
    <Dialog
      open={open}
      onOpenChange={(next) => {
        setOpen(next);
        setTyped("");
      }}
    >
      <DialogTrigger asChild>
        <Button variant="outline" className="text-danger">
          <Ban aria-hidden />
          Huỷ sự kiện
        </Button>
      </DialogTrigger>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Huỷ “{event.title}”?</DialogTitle>
          <DialogDescription>
            Sự kiện sẽ bị gỡ khỏi trang công khai và không thể mở lại.
            {event.status === "Published" ? " Người mua sẽ được hoàn tiền (spec 08)." : null}
          </DialogDescription>
        </DialogHeader>
        {cancel.isError ? <FormAlert message={toFriendlyMessage(cancel.error)} /> : null}
        <Field>
          <FieldLabel htmlFor="cancel-confirm">Gõ tên sự kiện để xác nhận</FieldLabel>
          <Input id="cancel-confirm" value={typed} autoComplete="off" onChange={(e) => setTyped(e.target.value)} />
        </Field>
        <DialogFooter>
          <Button variant="ghost" onClick={() => setOpen(false)}>
            Không huỷ
          </Button>
          <Button variant="destructive" disabled={!matches || cancel.isPending} onClick={() => cancel.mutate()}>
            Xác nhận huỷ
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
