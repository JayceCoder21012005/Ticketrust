import type { Metadata } from "next";
import { PreviewReceiver } from "@/features/organizer/wizard/review/preview-receiver";

export const metadata: Metadata = { title: "Xem trước", robots: { index: false, follow: false } };

/**
 * The iframe of the wizard's review step. Outside the (organizer) group on purpose: no AuthProvider, no sign-in — the
 * wizard posts the draft in, so this page never calls the api with credentials.
 */
export default function OrganizerPreviewPage() {
  return (
    <main id="main" className="flex flex-1 flex-col">
      <PreviewReceiver />
    </main>
  );
}
