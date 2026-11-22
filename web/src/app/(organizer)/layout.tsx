import type { Metadata } from "next";
import { AppProviders } from "@/components/app-providers";
import { AuthProvider } from "@/components/auth/auth-provider";
import { OrganizerGate } from "@/features/organizer/organizer-gate";

export const metadata: Metadata = { robots: { index: false, follow: false } };

/** Like the admin area: rendered only once the session is restored in the browser, so nothing to prerender for instant navigation. */
export const instant = false;

/** Spec 05 organizer area. proxy.ts sends visitors without a session hint to login; the api enforces the role. */
export default function OrganizerLayout({ children }: LayoutProps<"/">) {
  return (
    <AppProviders>
      <AuthProvider>
        <OrganizerGate>{children}</OrganizerGate>
      </AuthProvider>
    </AppProviders>
  );
}
