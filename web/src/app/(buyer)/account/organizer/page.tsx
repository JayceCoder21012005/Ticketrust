import type { Metadata } from "next";
import { OrganizerApplicationView } from "@/features/account/organizer-application";
import { t } from "@/lib/i18n.buyer";

export const metadata: Metadata = { title: t("accountHub.organizer.title"), robots: { index: false } };

/** Apply to become an organizer (spec 16). Sign-in is enforced by proxy.ts. */
export default function OrganizerApplicationRoute() {
  return <OrganizerApplicationView />;
}
