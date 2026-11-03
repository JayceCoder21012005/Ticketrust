import { Search, Ticket } from "lucide-react";
import Link from "next/link";
import { t } from "@/lib/i18n";
import { AccountLink } from "./account-link";
import { ThemeToggle } from "./theme-toggle";

/**
 * Global header. Search is a plain GET form so it works before hydration and keeps filters in the URL
 * (spec 04). The account link replaces "Đăng nhập" when the session hint cookie is present (spec 03).
 */
export function SiteHeader() {
  return (
    <header className="sticky top-0 z-40 border-b bg-background/90 backdrop-blur">
      <a
        href="#main"
        className="sr-only focus:not-sr-only focus:absolute focus:left-4 focus:top-2 focus:z-50 focus:rounded-md focus:bg-background focus:px-3 focus:py-2"
      >
        {t("common.skipToContent")}
      </a>
      <div className="mx-auto flex h-16 max-w-content items-center gap-4 px-4">
        <Link href="/" className="flex items-center gap-2 font-bold text-lg">
          <Ticket aria-hidden className="size-6 text-primary" />
          <span>TicketRush</span>
        </Link>
        <form role="search" action="/events" className="relative ml-auto hidden max-w-sm flex-1 sm:block">
          <Search aria-hidden className="pointer-events-none absolute left-3 top-1/2 size-4 -translate-y-1/2 text-muted-foreground" />
          <input
            type="search"
            name="q"
            aria-label={t("common.searchLabel")}
            placeholder={t("common.search")}
            className="h-9 w-full rounded-md border bg-background pl-9 pr-3 text-sm"
          />
        </form>
        <nav className="ml-auto flex items-center gap-1 sm:ml-0">
          <ThemeToggle />
          <AccountLink />
        </nav>
      </div>
    </header>
  );
}
