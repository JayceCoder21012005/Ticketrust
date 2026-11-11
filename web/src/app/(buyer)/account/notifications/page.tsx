import type { Metadata } from "next";
import { NotificationsPage } from "@/features/notifications/notifications-page";
import { t } from "@/lib/i18n.server";

export const metadata: Metadata = { title: t("notifications.title"), robots: { index: false } };

/** The buyer's whole inbox (spec 09). Sign-in is enforced by proxy.ts; the api only lists the caller's own notifications. */
export default function NotificationsRoute() {
  return <NotificationsPage />;
}
