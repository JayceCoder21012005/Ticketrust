import type { Metadata } from "next";
import { GateEvents } from "@/features/gate/gate-events";

export const metadata: Metadata = { title: "Soát vé" };

export default function GatePage() {
  return <GateEvents />;
}
