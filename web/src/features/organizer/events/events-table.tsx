"use client";

import { useQuery } from "@tanstack/react-query";
import { CalendarPlus, Search } from "lucide-react";
import Link from "next/link";
import { useState } from "react";
import { EmptyState } from "@/components/shared/empty-state";
import { ErrorState } from "@/components/shared/error-state";
import { Input } from "@/components/ui/input";
import { Skeleton } from "@/components/ui/skeleton";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import { toFriendlyMessage } from "@/lib/errors";
import { cn } from "@/lib/utils";
import { formatVietnam } from "@/lib/vn-time";
import { listEvents, organizerKeys } from "../api";
import { STATUS_LABELS, StatusBadge } from "../status-badge";
import type { EventStatus } from "../types";
import { useDebouncedValue } from "../use-debounced-value";

const TABS: (EventStatus | "all")[] = ["all", "Draft", "Published", "Ended", "Cancelled"];

export function EventsTable() {
  const [query, setQuery] = useState("");
  const [status, setStatus] = useState<EventStatus | "all">("all");
  const q = useDebouncedValue(query, 300);
  const filters = { q: q.trim(), status };
  const events = useQuery({ queryKey: organizerKeys.events(filters), queryFn: () => listEvents(filters), placeholderData: (previous) => previous });

  return (
    <div className="flex flex-col gap-4">
      <div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
        {/* A filter, not tabs: there is one list whose rows change, so toggle buttons describe it honestly. */}
        <div role="group" aria-label="Lọc theo trạng thái" className="inline-flex w-fit flex-wrap gap-1 rounded-lg bg-muted p-1">
          {TABS.map((tab) => (
            <button
              key={tab}
              type="button"
              aria-pressed={status === tab}
              onClick={() => setStatus(tab)}
              className={cn(
                "rounded-md px-3 py-1 text-sm font-medium text-muted-foreground hover:text-foreground",
                status === tab && "bg-background text-foreground shadow-sm",
              )}
            >
              {tab === "all" ? "Tất cả" : STATUS_LABELS[tab]}
            </button>
          ))}
        </div>
        <div className="relative sm:w-72">
          <Search aria-hidden className="pointer-events-none absolute top-1/2 left-2.5 size-4 -translate-y-1/2 text-muted-foreground" />
          <Input
            type="search"
            aria-label="Tìm sự kiện"
            placeholder="Tìm theo tên, nghệ sĩ…"
            className="pl-8"
            value={query}
            onChange={(e) => setQuery(e.target.value)}
          />
        </div>
      </div>

      {events.isPending ? (
        <Skeleton className="h-64 w-full" />
      ) : events.isError ? (
        <ErrorState description={toFriendlyMessage(events.error)} onRetry={() => void events.refetch()} />
      ) : events.data.length === 0 ? (
        <EmptyState
          icon={CalendarPlus}
          title={filters.q || status !== "all" ? "Không có sự kiện phù hợp" : "Chưa có sự kiện nào"}
          description={filters.q || status !== "all" ? "Thử từ khoá hoặc trạng thái khác." : "Bấm “Tạo sự kiện” để bắt đầu một bản nháp."}
        />
      ) : (
        <div className="rounded-xl border">
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Sự kiện</TableHead>
                <TableHead>Ngày diễn</TableHead>
                <TableHead className="hidden md:table-cell">Địa điểm</TableHead>
                <TableHead>Trạng thái</TableHead>
                <TableHead className="text-right">Số ghế</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {events.data.map((event) => (
                <TableRow key={event.id}>
                  <TableCell className="max-w-72 font-medium">
                    <Link href={`/organizer/events/${event.id}/info`} prefetch={false} className="line-clamp-2 hover:underline">
                      {event.title}
                    </Link>
                  </TableCell>
                  <TableCell className="whitespace-nowrap text-muted-foreground">{formatVietnam(event.startsAt)}</TableCell>
                  <TableCell className="hidden text-muted-foreground md:table-cell">{event.venueName}</TableCell>
                  <TableCell>
                    <span className="flex flex-wrap items-center gap-2">
                      <StatusBadge status={event.status} />
                      {event.status === "Published" || event.status === "Ended" ? (
                        <Link href={`/organizer/events/${event.id}/dashboard`} prefetch={false} className="text-xs text-primary hover:underline">
                          Bảng điều khiển
                        </Link>
                      ) : null}
                    </span>
                  </TableCell>
                  <TableCell className="text-right tabular-nums">{event.seatCount}</TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </div>
      )}
    </div>
  );
}
