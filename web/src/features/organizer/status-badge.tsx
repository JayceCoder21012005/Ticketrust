import { Badge } from "@/components/ui/badge";
import type { EventStatus } from "./types";

export const STATUS_LABELS: Record<EventStatus, string> = {
  Draft: "Nháp",
  Published: "Đã xuất bản",
  Ended: "Đã kết thúc",
  Cancelled: "Đã huỷ",
};

const VARIANTS = { Draft: "secondary", Published: "default", Ended: "outline", Cancelled: "destructive" } as const;

export function StatusBadge({ status }: { status: EventStatus }) {
  return <Badge variant={VARIANTS[status]}>{STATUS_LABELS[status]}</Badge>;
}
