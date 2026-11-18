import { Ticket } from "lucide-react";
import Link from "next/link";
import { NotificationBell } from "@/features/notifications/notification-bell";
import { SearchTrigger } from "@/features/search/search-trigger";
import { t } from "@/lib/i18n";
import { AccountLink } from "./account-link";
import { ThemeToggle } from "./theme-toggle";

/**
 * Global header. Search opens the ⌘K palette (UI refresh); the listing page keeps its GET form for the full results (spec 04).
 * The account link replaces "Đăng nhập" when the session hint cookie is present (spec 03).
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
        {/* No prefetch: the home page's JS would be downloaded on every page (counted in the 170 KB budget). */}
        <Link href="/" prefetch={false} className="flex items-center gap-2 font-bold text-lg">
          <Ticket aria-hidden className="size-6 text-primary" />
          <span>TicketRush</span>
        </Link>
        <div role="search" aria-label={t("common.searchLabel")} className="ml-auto flex min-w-0 max-w-sm flex-1 justify-end sm:justify-start">
          <SearchTrigger variant="header" />
        </div>
        <nav className="ml-auto flex items-center gap-1 sm:ml-0">
          <ThemeToggle />
          <NotificationBell />
          <AccountLink />
        </nav>
      </div>
    </header>
  );
}
