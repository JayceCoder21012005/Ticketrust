import { AppProviders } from "@/components/app-providers";
import { AuthProvider } from "@/components/auth/auth-provider";
import { SiteFooter } from "@/components/shared/site-footer";
import { SiteHeader } from "@/components/shared/site-header";
import { currentYear } from "@/lib/current-year";

/** Signed-in buyer area (account, tickets, checkout). proxy.ts redirects visitors without a session hint. */
export default async function BuyerLayout({ children }: LayoutProps<"/">) {
  return (
    <AppProviders>
      <AuthProvider>
        <SiteHeader />
        <main id="main" className="flex flex-1 flex-col">
          {children}
        </main>
        <SiteFooter year={await currentYear()} />
      </AuthProvider>
    </AppProviders>
  );
}
