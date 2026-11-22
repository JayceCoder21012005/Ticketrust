import type { Metadata } from "next";
import { Suspense } from "react";
import { Skeleton } from "@/components/ui/skeleton";
import { DashboardView } from "@/features/dashboard/dashboard-view";

export const metadata: Metadata = { title: "Bảng điều khiển", robots: { index: false } };

/** Spec 15: the live sale of one of the organizer's events. Ownership is enforced by the api (404) and the hub. */
type Params = { params: Promise<{ id: string }> };

export default function DashboardPage({ params }: Params) {
  return (
    <Suspense fallback={<Skeleton className="h-96 w-full" />}>
      <DashboardContent params={params} />
    </Suspense>
  );
}

async function DashboardContent({ params }: Params) {
  const { id } = await params;
  return <DashboardView eventId={id} />;
}
