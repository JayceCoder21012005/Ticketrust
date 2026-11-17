import type { Metadata } from "next";
import { ScannerScreen } from "@/features/gate/scanner-screen";

export const metadata: Metadata = { title: "Quét vé" };

export default async function GateScanPage({ params }: PageProps<"/gate/[eventId]">) {
  const { eventId } = await params;
  return <ScannerScreen key={eventId} eventId={eventId} />;
}
