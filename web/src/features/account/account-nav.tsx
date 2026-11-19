"use client";

import { Bell, Hourglass, LayoutDashboard, ReceiptText, Ticket, UserRound, type LucideIcon } from "lucide-react";
import Link from "next/link";
import { usePathname } from "next/navigation";
import { t } from "@/lib/i18n.buyer";
import { cn } from "@/lib/utils";

const ITEMS: { href: string; key: string; icon: LucideIcon }[] = [
  { href: "/account", key: "accountHub.nav.overview", icon: LayoutDashboard },
  { href: "/account/tickets", key: "accountHub.nav.tickets", icon: Ticket },
  { href: "/account/orders", key: "accountHub.nav.orders", icon: ReceiptText },
  { href: "/account/waitlist", key: "accountHub.nav.waitlist", icon: Hourglass },
  { href: "/account/notifications", key: "accountHub.nav.notifications", icon: Bell },
  { href: "/account/profile", key: "accountHub.nav.profile", icon: UserRound },
];

const isCurrent = (pathname: string, href: string) => (href === "/account" ? pathname === href : pathname === href || pathname.startsWith(`${href}/`));

/** The account hub's navigation (UI refresh): a sidebar on wide screens, a row of tabs that scrolls sideways on phones. */
export function AccountNav() {
  return <AccountNavLinks pathname={usePathname()} />;
}

/** The links themselves; with an empty pathname (the prerendered fallback) none is marked current. */
export function AccountNavLinks({ pathname }: { pathname: string }) {
  return (
    <nav aria-label={t("accountHub.navLabel")} className="-mx-4 overflow-x-auto px-4 lg:mx-0 lg:overflow-visible lg:px-0">
      <ul className="flex gap-1 lg:flex-col">
        {ITEMS.map(({ href, key, icon: Icon }) => {
          const current = isCurrent(pathname, href);
          return (
            <li key={href} className="shrink-0">
              <Link
                href={href}
                aria-current={current ? "page" : undefined}
                className={cn(
                  "flex h-10 items-center gap-2.5 whitespace-nowrap rounded-lg px-3 text-sm font-medium transition-colors outline-none focus-visible:ring-2 focus-visible:ring-ring",
                  current ? "bg-primary/10 text-primary" : "text-muted-foreground hover:bg-muted hover:text-foreground",
                )}
              >
                <Icon aria-hidden className="size-4" />
                {t(key)}
              </Link>
            </li>
          );
        })}
      </ul>
    </nav>
  );
}
