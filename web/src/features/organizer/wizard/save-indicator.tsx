"use client";

import { AlertCircle, Check, Loader2 } from "lucide-react";
import { Button } from "@/components/ui/button";
import type { AutosaveStatus } from "./use-autosave";

const time = new Intl.DateTimeFormat("vi-VN", { hour: "2-digit", minute: "2-digit", timeZone: "Asia/Ho_Chi_Minh" });

/** Header status of the step's autosave (design §3.3). Polite live region: announced without stealing focus. */
export function SaveIndicator({ status, errorCount, onReload }: { status: AutosaveStatus | null; errorCount: number; onReload: () => void }) {
  return (
    <div role="status" aria-live="polite" className="flex items-center gap-2 text-sm text-muted-foreground">
      {status ? <Content status={status} errorCount={errorCount} onReload={onReload} /> : null}
    </div>
  );
}

function Content({ status, errorCount, onReload }: { status: AutosaveStatus; errorCount: number; onReload: () => void }) {
  switch (status.state) {
    case "saving":
      return (
        <>
          <Loader2 aria-hidden className="size-4 animate-spin" />
          Đang lưu…
        </>
      );
    case "saved":
      return (
        <>
          <Check aria-hidden className="size-4 text-success" />
          Đã lưu lúc {status.savedAt ? time.format(status.savedAt) : ""}
        </>
      );
    case "invalid":
      return (
        <>
          <AlertCircle aria-hidden className="size-4 text-warning" />
          Chưa lưu: còn {Math.max(errorCount, 1)} lỗi
        </>
      );
    case "error":
      return status.conflict ? (
        <>
          <AlertCircle aria-hidden className="size-4 text-danger" />
          Sự kiện vừa được sửa ở nơi khác
          <Button size="sm" variant="outline" onClick={onReload}>
            Tải lại
          </Button>
        </>
      ) : (
        <>
          <AlertCircle aria-hidden className="size-4 text-danger" />
          Lưu thất bại
          <Button size="sm" variant="outline" onClick={status.retry}>
            Thử lại
          </Button>
        </>
      );
    default:
      return null;
  }
}
