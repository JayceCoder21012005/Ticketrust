import type { Metadata } from "next";
import { AppProviders } from "@/components/app-providers";
import { AuthProvider } from "@/components/auth/auth-provider";
import { GateGuard } from "@/features/gate/gate-guard";

export const metadata: Metadata = { robots: { index: false, follow: false } };

/** Gate check-in (spec 11): staff on a phone, no site header or footer. proxy.ts sends visitors without a session hint to login. */
export default function GateLayout({ children }: LayoutProps<"/">) {
  return (
    <AppProviders>
      <AuthProvider>
        <GateGuard>{children}</GateGuard>
      </AuthProvider>
    </AppProviders>
  );
}
