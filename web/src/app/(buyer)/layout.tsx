import { AppProviders } from "@/components/app-providers";
import { AuthProvider } from "@/components/auth/auth-provider";
import { ShoppingTimer } from "@/components/shared/shopping-timer";
import { CardSpotlight } from "@/components/shared/card-spotlight";
import { SiteFooter } from "@/components/shared/site-footer";
import { SiteHeader } from "@/components/shared/site-header";
import { UserHubProvider } from "@/features/notifications/use-user-hub";
import { currentYear } from "@/lib/current-year";

/** Signed-in buyer area (account, tickets, checkout). proxy.ts redirects visitors without a session hint. */
export default async function BuyerLayout({ children }: LayoutProps<"/">) {
  return (
    <AppProviders>
      <AuthProvider>
        {/* The tab's one live connection (notifications, order status): nothing below opens another. */}
        <UserHubProvider>
          <SiteHeader />
          <main id="main" className="flex flex-1 flex-col">
            {children}
          </main>
          <SiteFooter year={await currentYear()} />
      <CardSpotlight />
          <ShoppingTimer />
        </UserHubProvider>
      </AuthProvider>
    </AppProviders>
  );
}
