import type { Metadata } from "next";
import { AppProviders } from "@/components/app-providers";
import { AuthProvider } from "@/components/auth/auth-provider";
import { AdminGate } from "@/features/admin/admin-shell";

export const metadata: Metadata = { title: "Quản trị", robots: { index: false, follow: false } };

/** Spec 16 admin area. proxy.ts sends visitors to login; anyone but an admin sees the not-found page (and the api answers 404). */
export default function AdminLayout({ children }: LayoutProps<"/">) {
  return (
    <AppProviders>
      <AuthProvider>
        <AdminGate>{children}</AdminGate>
      </AuthProvider>
    </AppProviders>
  );
}
