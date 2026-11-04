import { Lock } from "lucide-react";
import { Alert, AlertDescription } from "@/components/ui/alert";

export const LOCKED_MESSAGE =
  "Đã khoá sau khi xuất bản để không thay đổi tồn kho đang bán. Muốn đổi, hãy huỷ và tạo lại sự kiện.";

export function LockedBanner() {
  return (
    <Alert>
      <Lock aria-hidden />
      <AlertDescription>{LOCKED_MESSAGE}</AlertDescription>
    </Alert>
  );
}

/** Lock icon next to a frozen field's label. */
export function LockedMark() {
  return <Lock role="img" aria-label="Đã khoá sau khi xuất bản" className="size-3.5 text-muted-foreground" />;
}
