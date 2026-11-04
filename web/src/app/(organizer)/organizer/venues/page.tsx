import type { Metadata } from "next";
import { PageHeader } from "@/components/shared/page-header";
import { OrganizerShell } from "@/features/organizer/organizer-shell";
import { CreateVenueDialog, VenuesTable } from "@/features/organizer/venues/venues-view";

export const metadata: Metadata = { title: "Địa điểm" };

export default function OrganizerVenuesPage() {
  return (
    <OrganizerShell breadcrumbs={[{ label: "Địa điểm" }]}>
      <div className="flex flex-col gap-6 p-4 md:p-6">
        <PageHeader title="Địa điểm" description="Địa điểm dùng chung và địa điểm riêng của bạn." actions={<CreateVenueDialog />} />
        <VenuesTable />
      </div>
    </OrganizerShell>
  );
}
