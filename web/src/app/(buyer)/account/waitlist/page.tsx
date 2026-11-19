import type { Metadata } from "next";
import { MyWaitlist } from "@/features/waitlist/my-waitlist";
import { t } from "@/lib/i18n.buyer";

export const metadata: Metadata = { title: t("waitlist.account.title"), robots: { index: false } };

/** The caller's live waitlist entries (spec 13). Sign-in is enforced by proxy.ts; the api lists only the caller's own. */
export default function WaitlistRoute() {
  return <MyWaitlist />;
}
