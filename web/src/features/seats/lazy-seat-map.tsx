"use client";

import dynamic from "next/dynamic";
import { Skeleton } from "@/components/ui/skeleton";

/** The map is the heaviest part of the page: load it on the client only, after the summary (spec 07). */
export const LazySeatMap = dynamic(() => import("./seat-map/seat-map").then((m) => m.SeatMap), {
  ssr: false,
  loading: () => <Skeleton className="aspect-[10/7] w-full rounded-xl" />,
});
