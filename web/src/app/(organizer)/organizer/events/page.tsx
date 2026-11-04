import type { Metadata } from "next";
import { PageHeader } from "@/components/shared/page-header";
import { CreateEventDialog } from "@/features/organizer/events/create-event-dialog";
import { EventsTable } from "@/features/organizer/events/events-table";
import { OrganizerShell } from "@/features/organizer/organizer-shell";

export const metadata: Metadata = { title: "Sự kiện của bạn" };

export default function OrganizerEventsPage() {
  return (
    <OrganizerShell breadcrumbs={[{ label: "Sự kiện" }]}>
      <div className="flex flex-col gap-6 p-4 md:p-6">
        <PageHeader title="Sự kiện" description="Bản nháp, sự kiện đang bán và đã kết thúc." actions={<CreateEventDialog />} />
        <EventsTable />
      </div>
    </OrganizerShell>
  );
}
