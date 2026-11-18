"use client";

import { useQuery } from "@tanstack/react-query";
import { QrCode } from "lucide-react";
import Link from "next/link";
import { EmptyState } from "@/components/shared/empty-state";
import { Skeleton } from "@/components/ui/skeleton";
import { listGateEvents } from "@/lib/gate/api";
import { formatVietnam } from "@/lib/vn-time";

/** The events the signed-in staff member is assigned to, tonight's first. */
export function GateEvents() {
  const events = useQuery({ queryKey: ["gate", "events"], queryFn: ({ signal }) => listGateEvents(signal) });

  return (
    <main id="main" className="mx-auto flex w-full max-w-xl flex-col gap-4 p-4">
      <h1 className="text-2xl font-bold">Soát vé</h1>
      {events.isPending ? <Skeleton className="h-24 w-full" /> : null}
      {events.isError ? <p role="alert">Không tải được danh sách sự kiện. Thử tải lại trang.</p> : null}
      {events.data?.length === 0 ? (
        <EmptyState icon={QrCode} title="Bạn chưa được phân công sự kiện nào" description="Nhờ nhà tổ chức thêm email của bạn vào danh sách nhân viên cổng." />
      ) : null}
      <ul className="flex flex-col gap-3">
        {events.data?.map((event) => (
          <li key={event.eventId}>
            <Link href={`/gate/${event.eventId}`} className="flex flex-col gap-1 rounded-xl border p-4 hover:bg-muted">
              <span className="text-lg font-semibold">{event.title}</span>
              <span className="text-sm text-muted-foreground">{formatVietnam(event.startsAt)}</span>
              <span className="text-sm text-muted-foreground">
                {event.venueName}, {event.city}
              </span>
              <span className="mt-1 text-sm font-medium">Cổng: {event.gateName}</span>
            </Link>
          </li>
        ))}
      </ul>
    </main>
  );
}
