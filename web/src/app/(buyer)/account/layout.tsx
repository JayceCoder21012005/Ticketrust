import { Suspense, type ReactNode } from "react";
import { AccountNav, AccountNavLinks } from "@/features/account/account-nav";

/**
 * The account hub (UI refresh): one navigation for overview, tickets, orders, notifications and profile; a sidebar on wide
 * screens, tabs above the page on phones. Sign-in is enforced by proxy.ts, like every buyer page.
 */
export default function AccountLayout({ children }: { children: ReactNode }) {
  return (
    <div className="mx-auto flex w-full max-w-content flex-col gap-6 px-4 py-6 lg:grid lg:grid-cols-[13rem_minmax(0,1fr)] lg:gap-10 lg:py-10">
      <aside className="lg:sticky lg:top-24 lg:self-start">
        {/* The current page comes from the url, which only exists at request time: the links prerender without it. */}
        <Suspense fallback={<AccountNavLinks pathname="" />}>
          <AccountNav />
        </Suspense>
      </aside>
      <div className="min-w-0">{children}</div>
    </div>
  );
}
