import type { Metadata } from "next";
import { PageHeader } from "@/components/shared/page-header";
import { DashboardPicker } from "@/features/dashboard/dashboard-picker";
import { OrganizerShell } from "@/features/organizer/organizer-shell";

export const metadata: Metadata = { title: "Dashboard" };

/** Spec 15: choose an event to watch its sale live (or its history once it ended). */
export default function OrganizerDashboardPage() {
  return (
    <OrganizerShell breadcrumbs={[{ label: "Dashboard" }]}>
      <div className="flex flex-col gap-6 p-4 md:p-6">
        <PageHeader title="Dashboard" description="Chọn một sự kiện để xem doanh thu, hàng chờ và vé bán theo thời gian thực." />
        <DashboardPicker />
      </div>
    </OrganizerShell>
  );
}
