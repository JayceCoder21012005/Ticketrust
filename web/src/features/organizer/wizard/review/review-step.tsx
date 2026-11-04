"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Check, ExternalLink, Lock, Rocket, X } from "lucide-react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { useState } from "react";
import { toast } from "sonner";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { Skeleton } from "@/components/ui/skeleton";
import { formatOpensIn } from "@/lib/clock";
import { ApiError, toFriendlyMessage } from "@/lib/errors";
import { formatVietnam } from "@/lib/vn-time";
import { getChecklist, listVenues, organizerKeys, publishEvent } from "../../api";
import { CancelEventDialog } from "../../events/cancel-event-dialog";
import { toEventDetail } from "../../to-event-detail";
import type { PublishFailure, WizardStep } from "../../types";
import type { StepProps } from "../step-props";
import { PreviewFrame } from "./preview-frame";

/** Every publish rule (api `PublishChecklist.Rules`, same order) with its Vietnamese label and the step that fixes it. */
const RULES: { code: string; label: string; step: Exclude<WizardStep, "review"> }[] = [
  { code: "publish.missing_cover", label: "Có ảnh cover", step: "info" },
  { code: "publish.no_tiers", label: "Có ít nhất một hạng vé", step: "tiers" },
  { code: "publish.tier_without_seats", label: "Mỗi hạng ngồi đều có ghế", step: "layout" },
  { code: "publish.ga_without_capacity", label: "Mỗi hạng đứng có sức chứa", step: "tiers" },
  { code: "publish.no_phases", label: "Có ít nhất một đợt mở bán", step: "phases" },
  { code: "publish.first_phase_too_soon", label: "Đợt đầu mở sau ít nhất 10 phút", step: "phases" },
  { code: "event.phase_overlap", label: "Các đợt mở bán không chồng giờ", step: "phases" },
  { code: "phase.after_event_start", label: "Các đợt mở bán kết thúc trước giờ diễn", step: "phases" },
];

/** Step 5 (design §3.3): checklist with links back, the buyer's view of the page, what gets frozen, then publish. */
export function ReviewStep({ event, onSaved }: StepProps) {
  const queryClient = useQueryClient();
  const router = useRouter();
  const checklist = useQuery({ queryKey: organizerKeys.checklist(event.id), queryFn: () => getChecklist(event.id) });
  const venues = useQuery({ queryKey: organizerKeys.venues, queryFn: listVenues });
  const [confirming, setConfirming] = useState(false);
  const publish = useMutation({
    mutationFn: () => publishEvent(event.id),
    onSuccess: (published) => {
      setConfirming(false);
      onSaved(published);
      toast.success("Đã xuất bản", {
        action: { label: "Xem trang công khai", onClick: () => router.push(`/events/${published.slug}`) },
      });
    },
    onError: () => void queryClient.invalidateQueries({ queryKey: organizerKeys.checklist(event.id) }),
  });

  // The checklist gates publishing only; a published event's locked steps can't be "fixed", so show no failures.
  const failures = event.status === "Draft" ? (checklist.data?.failures ?? []) : [];
  const failed = (code: string) => failures.find((f) => f.code === code);
  const publishFailures = publish.error instanceof ApiError ? ((publish.error.problem.failures as PublishFailure[] | undefined) ?? []) : [];
  const detail = toEventDetail(event, venues.data?.find((v) => v.id === event.venueId));
  const editable = event.status === "Draft" || event.status === "Published";

  return (
    <div className="flex flex-col gap-8">
      <section aria-labelledby="checklist-title" className="flex flex-col gap-3">
        <h2 id="checklist-title" className="text-lg font-semibold">
          Điều kiện xuất bản
        </h2>
        {checklist.isPending ? (
          <Skeleton className="h-48" />
        ) : (
          <ul className="flex flex-col divide-y rounded-xl border">
            {RULES.map((rule) => {
              const failure = failed(rule.code);
              return (
                <li key={rule.code} className="flex items-center gap-3 px-4 py-3">
                  {failure ? (
                    <X role="img" aria-label="Chưa đạt" className="size-5 shrink-0 text-danger" />
                  ) : (
                    <Check role="img" aria-label="Đạt" className="size-5 shrink-0 text-success" />
                  )}
                  <span className="flex-1">{rule.label}</span>
                  {failure ? (
                    <Link
                      href={`/organizer/events/${event.id}/${rule.step}`}
                      prefetch={false}
                      aria-label={`Sửa: ${rule.label}`}
                      className="text-sm font-medium text-primary hover:underline"
                    >
                      Sửa
                    </Link>
                  ) : null}
                </li>
              );
            })}
          </ul>
        )}
      </section>

      <section aria-labelledby="preview-title" className="flex flex-col gap-3">
        <h2 id="preview-title" className="text-lg font-semibold">
          Người mua sẽ thấy
        </h2>
        <PreviewFrame eventId={event.id} detail={detail} />
      </section>

      {event.status === "Draft" ? (
        <>
          <Alert>
            <Lock aria-hidden />
            <AlertTitle>Sau khi xuất bản</AlertTitle>
            <AlertDescription>
              Giá vé, sức chứa, sơ đồ ghế và các đợt mở bán sẽ bị khoá để không thay đổi tồn kho đang bán. Tên, giới thiệu và ảnh cover vẫn sửa được.
            </AlertDescription>
          </Alert>
          {publish.isError ? (
            <Alert variant="destructive" role="alert">
              <AlertDescription>
                {toFriendlyMessage(publish.error)}
                {publishFailures.length > 0 ? ` (${publishFailures.map((f) => RULES.find((r) => r.code === f.code)?.label ?? f.code).join(", ")})` : null}
              </AlertDescription>
            </Alert>
          ) : null}
          <div className="flex flex-wrap gap-3">
            <Button size="lg" disabled={!checklist.data?.ready || publish.isPending} onClick={() => setConfirming(true)}>
              <Rocket aria-hidden />
              Xuất bản
            </Button>
            <CancelEventDialog event={event} onCancelled={onSaved} />
          </div>
        </>
      ) : (
        <div className="flex flex-wrap items-center gap-3">
          {event.status === "Published" ? (
            <Button asChild variant="outline">
              <Link href={`/events/${event.slug}`} prefetch={false}>
                <ExternalLink aria-hidden />
                Xem trang công khai
              </Link>
            </Button>
          ) : null}
          {editable ? <CancelEventDialog event={event} onCancelled={onSaved} /> : null}
        </div>
      )}

      <Dialog open={confirming} onOpenChange={setConfirming}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Xuất bản “{event.title}”?</DialogTitle>
            <DialogDescription>Sự kiện sẽ hiện trên trang công khai ngay.</DialogDescription>
          </DialogHeader>
          <PublishSummary event={event} />
          <DialogFooter>
            <Button variant="ghost" onClick={() => setConfirming(false)}>
              Để sau
            </Button>
            <Button disabled={publish.isPending} onClick={() => publish.mutate()}>
              {publish.isPending ? "Đang xuất bản…" : "Xuất bản ngay"}
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </div>
  );
}

function PublishSummary({ event }: { event: StepProps["event"] }) {
  const seats = Object.values(event.seatsPerTier).reduce((sum, n) => sum + n, 0);
  const first = [...event.phases].sort((a, b) => Date.parse(a.opensAt) - Date.parse(b.opensAt))[0];
  const [now] = useState(() => Date.now());

  return (
    <dl className="grid grid-cols-[auto_1fr] gap-x-4 gap-y-2 text-sm">
      <dt className="text-muted-foreground">Số ghế</dt>
      <dd>{seats.toLocaleString("vi-VN")}</dd>
      <dt className="text-muted-foreground">Hạng vé</dt>
      <dd>{event.tiers.length}</dd>
      {first ? (
        <>
          <dt className="text-muted-foreground">Mở bán đầu tiên</dt>
          <dd>
            {formatVietnam(first.opensAt)}
            {Date.parse(first.opensAt) > now ? ` · sau ${formatOpensIn(Date.parse(first.opensAt) - now)}` : null}
          </dd>
        </>
      ) : null}
    </dl>
  );
}
