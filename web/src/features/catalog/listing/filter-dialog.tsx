"use client";

import { SlidersHorizontal, X } from "lucide-react";
import { useEffect, useRef, useState, type ReactNode } from "react";
import { Button } from "@/components/ui/button";
import { t } from "@/lib/i18n";

type FilterDialogProps = {
  activeCount: number;
  children: ReactNode;
};

/**
 * Phone filter sheet. A native `<dialog>` (modal: focus trap, Esc, inert background) instead of a Radix Sheet,
 * which would cost the listing ~20 KB of first-load JS against the 170 KB budget. The panel inside is only
 * rendered while open: the desktop sidebar already has one, and hydrating a second copy costs blocking time.
 */
export function FilterDialog({ activeCount, children }: FilterDialogProps) {
  const dialog = useRef<HTMLDialogElement>(null);
  const [open, setOpen] = useState(false);
  const label = activeCount > 0 ? t("catalog.listing.filtersCount", "vi", { count: activeCount }) : t("catalog.listing.filters");

  useEffect(() => {
    if (open && !dialog.current?.open) {
      dialog.current?.showModal();
    }
  }, [open]);

  const close = () => dialog.current?.close();

  return (
    <>
      <Button variant="outline" onClick={() => setOpen(true)} aria-haspopup="dialog">
        <SlidersHorizontal aria-hidden />
        {label}
      </Button>
      <dialog
        ref={dialog}
        aria-labelledby="filter-dialog-title"
        className="m-0 mt-auto max-h-[85dvh] w-full max-w-none rounded-t-2xl border-t bg-background p-0 text-foreground backdrop:bg-black/50 open:flex open:flex-col"
        onClose={() => setOpen(false)}
        // A click on the backdrop lands on the <dialog> element itself.
        onClick={(e) => e.target === e.currentTarget && close()}
      >
        {open ? (
          <>
            <div className="flex items-center justify-between border-b px-4 py-3">
              <h2 id="filter-dialog-title" className="text-base font-semibold">
                {t("catalog.listing.filters")}
              </h2>
              <Button variant="ghost" size="icon" onClick={close} aria-label={t("common.close")}>
                <X aria-hidden />
              </Button>
            </div>
            <div className="overflow-y-auto px-4 py-4">{children}</div>
            <div className="border-t px-4 py-3 pb-[max(0.75rem,env(safe-area-inset-bottom))]">
              <Button className="w-full" size="lg" onClick={close}>
                {t("catalog.listing.showResults")}
              </Button>
            </div>
          </>
        ) : null}
      </dialog>
    </>
  );
}
