import type { ReactNode } from "react";
import { cn } from "@/lib/utils";

type StickyActionBarProps = {
  /** Accessible name of the region, e.g. "Mua vé". */
  label: string;
  children: ReactNode;
  className?: string;
};

/** Mobile bottom CTA (Luma-style). Hidden from `md` up, where the CTA lives in the page layout. */
export function StickyActionBar({ label, children, className }: StickyActionBarProps) {
  return (
    <section
      aria-label={label}
      className={cn(
        "fixed inset-x-0 bottom-0 z-40 flex items-center gap-3 border-t bg-background/95 px-4 py-3 pb-[max(0.75rem,env(safe-area-inset-bottom))] backdrop-blur md:hidden",
        className,
      )}
    >
      {children}
    </section>
  );
}
