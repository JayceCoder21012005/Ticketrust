"use client";

import { useQuery } from "@tanstack/react-query";
import { LayoutDashboard } from "lucide-react";
import Link from "next/link";
import { EmptyState } from "@/components/shared/empty-state";
import { ErrorState } from "@/components/shared/error-state";
import { Card, CardContent } from "@/components/ui/card";
import { Skeleton } from "@/components/ui/skeleton";
import { listEvents, organizerKeys } from "@/features/organizer/api";
import { StatusBadge } from "@/features/organizer/status-badge";
import type { OrganizerEventItem } from "@/features/organizer/types";
import { toFriendlyMessage } from "@/lib/errors";

const ALL = { q: "", status: "all" as const };

/** Events with something to watch: on sale (or taken down by an admin) first, then those that ended. */
const watchable = (events: OrganizerEventItem[]) =>
  events
    .filter(
      (e) =>
        e.status === "Published" ||
        e.status === "Ended" ||
        e.status === "Suspended",
    )
    .sort((a, b) =>
      (a.status === "Ended") === (b.status === "Ended")
        ? a.startsAt.localeCompare(b.startsAt)
        : a.status === "Ended"
          ? 1
          : -1,
    );

const when = (iso: string) =>
  new Date(iso).toLocaleString("vi-VN", {
    dateStyle: "medium",
    timeStyle: "short",
    timeZone: "Asia/Ho_Chi_Minh",
  });

/** `/organizer/dashboard` (spec 15): pick the event whose live sale (or history) to open. */
export function DashboardPicker() {
  const events = useQuery({
    queryKey: organizerKeys.events(ALL),
    queryFn: () => listEvents(ALL),
  });

  if (events.isPending) return <Skeleton className="h-64 w-full" />;
  if (events.isError)
    return (
      <ErrorState
        title="Không tải được danh sách sự kiện"
        description={toFriendlyMessage(events.error)}
        onRetry={() => void events.refetch()}
      />
    );

  const list = watchable(events.data);
  if (list.length === 0) {
    return (
      <EmptyState
        icon={LayoutDashboard}
        title="Chưa có sự kiện để theo dõi"
        description="Bảng điều khiển có dữ liệu khi sự kiện đã được đăng bán."
        action={
          <Link
            href="/organizer/events"
            className="text-sm font-medium text-primary hover:underline"
          >
            Đến danh sách sự kiện
          </Link>
        }
      />
    );
  }

  return (
    <ul className="grid gap-3 sm:grid-cols-2 xl:grid-cols-3">
      {list.map((event) => (
        <li key={event.id}>
          <Card className="relative h-full transition-colors hover:border-primary">
            <CardContent className="flex h-full flex-col gap-2">
              <div className="flex items-start justify-between gap-2">
                <Link
                  href={`/organizer/events/${event.id}/dashboard`}
                  prefetch={false}
                  className="font-semibold after:absolute after:inset-0 hover:underline"
                >
                  {event.title}
                </Link>
                <StatusBadge status={event.status} />
              </div>
              <p className="text-sm text-muted-foreground">{`${when(event.startsAt)} · ${event.venueName}`}</p>
            </CardContent>
          </Card>
        </li>
      ))}
    </ul>
  );
}
