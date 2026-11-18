"use client";

import { Search } from "lucide-react";
import dynamic from "next/dynamic";
import { useEffect, useState } from "react";
import { t } from "@/lib/i18n";
import { cn } from "@/lib/utils";

// The palette (cmdk, dialog) is downloaded on first intent, so it never counts against a public page's JS budget.
const loadPalette = () => import("./search-palette");
const SearchPalette = dynamic(loadPalette, { ssr: false });

/**
 * Opens the ⌘K search palette (UI refresh): a click, ⌘K / Ctrl+K anywhere, or "/" outside a text field. Hovering or focusing it
 * starts downloading the palette so it opens without a wait.
 */
export function SearchTrigger({ variant, className }: { variant: "header" | "hero"; className?: string }) {
  const [open, setOpen] = useState(false);
  const [wanted, setWanted] = useState(false);

  useEffect(() => {
    const onKey = (event: KeyboardEvent) => {
      const typing = event.target instanceof HTMLElement && (event.target.isContentEditable || ["INPUT", "TEXTAREA", "SELECT"].includes(event.target.tagName));
      if ((event.key.toLowerCase() === "k" && (event.metaKey || event.ctrlKey)) || (event.key === "/" && !typing)) {
        event.preventDefault();
        setWanted(true);
        setOpen((value) => !value || event.key === "/");
      }
    };
    document.addEventListener("keydown", onKey);
    return () => document.removeEventListener("keydown", onKey);
  }, []);

  const warm = () => void loadPalette();

  return (
    <>
      <button
        type="button"
        onClick={() => {
          setWanted(true);
          setOpen(true);
        }}
        onPointerEnter={warm}
        onFocus={warm}
        aria-haspopup="dialog"
        className={cn(
          "group flex items-center gap-2 rounded-full border bg-background/80 text-left text-muted-foreground shadow-xs backdrop-blur transition-colors hover:border-primary/40 hover:text-foreground focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-ring",
          // On a phone the header button is just the icon (its text stays the accessible name), so the header never overflows.
          variant === "header"
            ? "h-9 w-9 min-w-0 justify-center px-0 text-sm sm:w-full sm:max-w-sm sm:justify-start sm:px-3"
            : "h-14 w-full min-w-0 max-w-2xl px-5 text-base shadow-lg ring-1 ring-primary/20",
          className,
        )}
      >
        <Search aria-hidden className={variant === "header" ? "size-4" : "size-5 text-primary"} />
        <span className={cn("min-w-0 flex-1 truncate", variant === "header" && "sr-only sm:not-sr-only")}>{t("common.search")}</span>
        <kbd aria-hidden className="hidden rounded border bg-muted px-1.5 py-0.5 font-mono text-[11px] font-medium sm:inline">
          Ctrl K
        </kbd>
      </button>
      {wanted ? <SearchPalette open={open} onOpenChange={setOpen} /> : null}
    </>
  );
}
